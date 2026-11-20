using System.ComponentModel;
using System.Globalization;
using System.Security;
using System.Text.Json;

namespace Pane.Services.Traffic;

internal sealed record TrafficHistoryRow(DateTimeOffset Bucket, TrafficProcessIdentity Process, long Download, long Upload);
internal sealed record TrafficHistoryApplicationRow(string Name, string? Path, decimal Download, decimal Upload);

internal readonly record struct TrafficHistoryTotals(decimal Download, decimal Upload);

internal enum TrafficHistoryState { Available, NotInstalled, Unavailable, AccessDenied, CursorExpired, RangeTooLarge, ApplicationSummaryUnsupported, ApplicationOptionsUnsupported }

internal enum TrafficHistoryCoverage { Unavailable, Complete, Gap }

internal sealed record TrafficHistoryPage(TrafficHistoryState State, IReadOnlyList<TrafficHistoryRow> Rows,
    TrafficHistoryCoverage Coverage = TrafficHistoryCoverage.Unavailable, DateTimeOffset? CoveredThrough = null, string? Cursor = null,
    TrafficHistoryTotals? Totals = null, IReadOnlyList<TrafficHistoryApplicationRow>? Applications = null);

internal static class TrafficHistoryClient
{
    internal static TrafficHistoryQuery? CalendarQuery(DateOnly first, DateOnly last, DateTimeOffset now, TimeZoneInfo zone,
        string? processInstanceId, out bool includesPartialBuckets)
    {
        includesPartialBuckets = false;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        if (first > last || last > today || first < today.AddDays(-729)) return null;

        var from = StartOfDay(first, zone);
        var to = last == today ? now : StartOfDay(last.AddDays(1), zone);
        if (from >= to) return null;
        var resolution = from < now.AddDays(-90) ? 86400 : from < now.AddHours(-48) ? 3600 : 60;
        var startSeconds = from.ToUnixTimeSeconds();
        var aligned = DateTimeOffset.FromUnixTimeSeconds(startSeconds - startSeconds % resolution);
        includesPartialBuckets = resolution > 60 && (from != aligned || last != today && to.ToUnixTimeSeconds() % resolution != 0);
        return new(aligned, to, resolution, processInstanceId);
    }

    static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var wallTime = day.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(wallTime)) wallTime = wallTime.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(wallTime) ? zone.GetAmbiguousTimeOffsets(wallTime).Max() : zone.GetUtcOffset(wallTime);
        return new DateTimeOffset(wallTime, offset).ToUniversalTime();
    }

    public static async Task<TrafficHistoryPage> ReadAsync(TrafficHistoryQuery query, string? cursor, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            if (!TrafficServiceIdentity.IsInstalled()) return new(TrafficHistoryState.NotInstalled, []);
            using var pipe = await TrafficPipeProtocol.ConnectAsync(deadline.Token).ConfigureAwait(false);
            return await ReadPageAsync(pipe, query, cursor, deadline.Token).ConfigureAwait(false);
        }
        catch (TrafficPipeAccessDeniedException)
        {
            return new(TrafficHistoryState.AccessDenied, []);
        }
        catch (TrafficRequestException error) when (error.Code is "cursor-expired" or "history-query-too-large-narrow-range")
        {
            return new(error.Code == "cursor-expired" ? TrafficHistoryState.CursorExpired : TrafficHistoryState.RangeTooLarge, []);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(TrafficHistoryState.Unavailable, []);
        }
        catch (Exception error) when (IsRequestFailure(error))
        {
            return new(TrafficHistoryState.Unavailable, []);
        }
    }

    internal static async Task<TrafficHistoryPage> ReadPageAsync(TrafficPipeProtocol pipe, TrafficHistoryQuery query, string? cursor, CancellationToken token)
    {
        var page = await pipe.RequestAsync("history", token, limit: 40, history: query, cursor: cursor).ConfigureAwait(false);
        return ParsePage(page, query);
    }

    internal static TrafficHistoryPage ParsePage(JsonElement page, TrafficHistoryQuery query)
    {
        var grouped = page.TryGetProperty("groupBy", out var grouping) && grouping.GetString() == "application";
        if (query.ByApplication && !grouped) return new(TrafficHistoryState.ApplicationSummaryUnsupported, []);
        if (query.ByApplication && query.HasApplicationOptions &&
            (!page.TryGetProperty("applicationOptions", out var options) || options.ValueKind != JsonValueKind.True))
            return new(TrafficHistoryState.ApplicationOptionsUnsupported, []);
        if (grouped && (!query.ByApplication || query.ProcessInstanceId != null)) throw new InvalidDataException("Unexpected history grouping.");
        var coverage = page.GetProperty("coverageSummary");
        var state = coverage.GetProperty("state").GetString() switch
        {
            "complete" => TrafficHistoryCoverage.Complete,
            "gap" => TrafficHistoryCoverage.Gap,
            "unavailable" => TrafficHistoryCoverage.Unavailable,
            _ => throw new InvalidDataException("Unexpected traffic history coverage.")
        };
        var rows = new List<TrafficHistoryRow>();
        var applications = grouped ? new List<TrafficHistoryApplicationRow>() : null;
        foreach (var row in page.GetProperty("rows").EnumerateArray())
        {
            if (applications != null)
            {
                applications.Add(new(row.GetProperty("name").GetString() ?? throw new InvalidDataException("Missing application name."),
                    row.GetProperty("imagePath").GetString(), ReadTotal(row, "rxBytes"), ReadTotal(row, "txBytes")));
                continue;
            }
            var process = TrafficProcessIdentity.Read(row);
            if (query.ProcessInstanceId is { } expected && process.InstanceId != expected)
                throw new InvalidDataException("Unexpected process in filtered traffic history.");
            rows.Add(new(row.GetProperty("bucketStart").GetDateTimeOffset(), process, ReadBytes(row, "rxBytes"), ReadBytes(row, "txBytes")));
        }
        TrafficHistoryTotals? totals = page.TryGetProperty("recordedTotals", out var recorded)
            ? new(ReadTotal(recorded, "rxBytes"), ReadTotal(recorded, "txBytes")) : null;
        return new(TrafficHistoryState.Available, rows, state,
            state == TrafficHistoryCoverage.Unavailable ? null : coverage.GetProperty("end").GetDateTimeOffset(),
            page.GetProperty("nextCursor").GetString(), totals, applications);
    }

    public static async Task<bool> ClearAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            if (!TrafficServiceIdentity.IsInstalled()) return false;
            using var pipe = await TrafficPipeProtocol.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var response = await pipe.RequestAsync("clearHistory", deadline.Token, confirm: true).ConfigureAwait(false);
            return response.GetProperty("scope").GetString() == "own" && response.GetProperty("cleared").GetBoolean();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (Exception error) when (IsRequestFailure(error)) { return false; }
    }

    static bool IsRequestFailure(Exception error) => error is IOException or UnauthorizedAccessException or SecurityException or Win32Exception or
        JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException;

    static long ReadBytes(JsonElement row, string property) =>
        long.TryParse(row.GetProperty(property).GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            ? bytes : throw new InvalidDataException("Invalid history byte count.");

    static decimal ReadTotal(JsonElement totals, string property) =>
        decimal.TryParse(totals.GetProperty(property).GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            ? bytes : throw new InvalidDataException("Invalid history byte total.");
}
