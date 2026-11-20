using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text.Json;

namespace Pane.Services.Traffic;

internal enum ProcessTrafficState { NotInstalled, Unavailable, AccessDenied, PermissionDenied, Disabled, Sampling, Partial, Live }

internal sealed record TrafficProcessIdentity(string InstanceId, string Name, int Pid, long CreatedFileTime = 0, string? ImagePath = null)
{
    internal static TrafficProcessIdentity Read(JsonElement row)
    {
        var id = row.GetProperty("processInstanceId").GetString();
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("Missing traffic process identity.");
        var pid = row.GetProperty("pid").GetInt32();
        var name = row.GetProperty("name").GetString();
        var created = row.TryGetProperty("createdFileTime", out var time) && time.ValueKind == JsonValueKind.String &&
            long.TryParse(time.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ? ticks : 0;
        var path = row.TryGetProperty("imagePath", out var image) && image.ValueKind == JsonValueKind.String ? image.GetString() : null;
        return new(id, string.IsNullOrWhiteSpace(name) ? $"PID {pid}" : name, pid, created, path);
    }
}

internal readonly record struct TrafficAppRun(long Download, long Upload, bool HasGap);

internal sealed record TrafficAppGroup(string RunId, int MemberCount, TrafficProcessIdentity[] Members);

internal sealed record ProcessTrafficRow(TrafficProcessIdentity Process, double? Download, double? Upload, TrafficAppRun? AppRun,
    TrafficAppGroup? AppGroup = null)
{
    internal string Key => AppGroup is { } group ? $"app:{group.RunId}" : Process.InstanceId;
}

internal static class TrafficRankingOrder
{
    internal static IEnumerable<ProcessTrafficRow> Preserve(IEnumerable<ProcessTrafficRow> sorted, IEnumerable<string> previousIds)
    {
        var rows = sorted.ToArray();
        var remaining = rows.ToDictionary(row => row.Key, StringComparer.Ordinal);
        foreach (var id in previousIds)
            if (remaining.Remove(id, out var row)) yield return row;
        foreach (var row in rows)
            if (remaining.ContainsKey(row.Key)) yield return row;
    }
}

internal sealed record ProcessTrafficSample(ProcessTrafficState State, IReadOnlyList<ProcessTrafficRow> Rows, bool? Enabled = null,
    bool SettingsWriteFailed = false);

internal sealed class ProcessTrafficClient : IDisposable
{
    internal const int DetailRankingLimit = 12;
    TrafficPipeProtocol? protocol;
    string? collectorEpoch;
    int generation;
    long lastRenewed;
    ProcessTrafficSample? offlineSample;

    public async Task<ProcessTrafficSample> SampleAsync(bool? requestedEnabled, int snapshotLimit, CancellationToken token,
        TrafficRankingQuery? ranking = null, bool retry = false)
    {
        if (!retry && requestedEnabled == null && offlineSample is { } offline) return offline;
        offlineSample = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (protocol == null)
            {
                if (!TrafficServiceIdentity.IsInstalled()) return Offline(ProcessTrafficState.NotInstalled);
                protocol = await TrafficPipeProtocol.ConnectAsync(deadline.Token).ConfigureAwait(false);
            }

            var status = await protocol.RequestAsync("status", deadline.Token).ConfigureAwait(false);
            var isEnabled = status.GetProperty("enabled").GetBoolean();
            var writeFailed = false;
            if (requestedEnabled is { } enabled)
            {
                try
                {
                    var result = await protocol.RequestAsync("scope.setEnabled", deadline.Token, enabled: enabled).ConfigureAwait(false);
                    isEnabled = result.GetProperty("enabled").GetBoolean();
                }
                catch (TrafficRequestException error) when (error.Code == "scope-write-failed")
                {
                    writeFailed = true;
                    status = await protocol.RequestAsync("status", deadline.Token).ConfigureAwait(false);
                    isEnabled = status.GetProperty("enabled").GetBoolean();
                }
            }

            if (!isEnabled)
            {
                collectorEpoch = null;
                return new(ProcessTrafficState.Disabled, [], Enabled: false, SettingsWriteFailed: writeFailed);
            }

            try
            {
                if (collectorEpoch == null)
                    await AcquireLeaseAsync(protocol, deadline.Token).ConfigureAwait(false);
                else if (!status.GetProperty("hasLease").GetBoolean())
                    return Offline(ProcessTrafficState.Unavailable);
                else if (Stopwatch.GetElapsedTime(lastRenewed) >= TimeSpan.FromSeconds(5))
                {
                    await protocol.RequestAsync("lease.renew", deadline.Token, generation: generation, collectorEpoch: collectorEpoch).ConfigureAwait(false);
                    lastRenewed = Stopwatch.GetTimestamp();
                }
            }
            catch (TrafficRequestException error) when (error.Code == "scope-disabled")
            {
                collectorEpoch = null;
                return new(ProcessTrafficState.Disabled, [], Enabled: false, SettingsWriteFailed: writeFailed);
            }

            if (snapshotLimit == 0) return new(ProcessTrafficState.Sampling, [], Enabled: true, SettingsWriteFailed: writeFailed);
            return (await ReadSnapshotAsync(ranking, snapshotLimit, deadline.Token).ConfigureAwait(false)) with { SettingsWriteFailed = writeFailed };
        }
        catch (TrafficPipeAccessDeniedException)
        {
            return Offline(ProcessTrafficState.AccessDenied);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Offline(ProcessTrafficState.Unavailable);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or Win32Exception or
            JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
        {
            return Offline(ProcessTrafficState.Unavailable);
        }
    }

    async Task AcquireLeaseAsync(TrafficPipeProtocol connected, CancellationToken token)
    {
        var lease = await connected.RequestAsync("lease.acquire", token).ConfigureAwait(false);
        generation = lease.GetProperty("generation").GetInt32();
        collectorEpoch = lease.GetProperty("collectorEpoch").GetString() ?? throw new InvalidDataException("Missing collector epoch.");
        lastRenewed = Stopwatch.GetTimestamp();
    }

    internal async Task<ProcessTrafficSample> ReadRankingAsync(TrafficRankingQuery ranking, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try { return await ReadSnapshotAsync(ranking, DetailRankingLimit, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Offline(ProcessTrafficState.Unavailable); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or Win32Exception or
            JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
        {
            return Offline(ProcessTrafficState.Unavailable);
        }
    }

    async Task<ProcessTrafficSample> ReadSnapshotAsync(TrafficRankingQuery? ranking, int limit, CancellationToken token)
    {
        if (protocol == null) return new(ProcessTrafficState.Unavailable, []);
        try
        {
            var operation = ranking is { ActiveOnly: true } ? "snapshot.ranking.active" : ranking.HasValue ? "snapshot.ranking" : "snapshot";
            var snapshot = await protocol.RequestAsync(operation,
                token, limit: limit, firstPageOnly: true, ranking: ranking).ConfigureAwait(false);
            return ParseSnapshot(snapshot);
        }
        catch (TrafficRequestException error) when (ranking.HasValue && error.Code == "unsupported-operation")
        {
            return new(ProcessTrafficState.Unavailable, [], Enabled: true);
        }
    }

    internal static ProcessTrafficSample ParseSnapshot(JsonElement snapshot)
    {
        var quality = snapshot.GetProperty("quality");
        if (!snapshot.GetProperty("collecting").GetBoolean())
        {
            var permissionDenied = quality.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String &&
                reason.GetString() == "etw-unavailable:UnauthorizedAccessException";
            return new(permissionDenied ? ProcessTrafficState.PermissionDenied : ProcessTrafficState.Unavailable, [], Enabled: true);
        }

        var state = quality.GetProperty("state").GetString() == "good" ? ProcessTrafficState.Live :
            snapshot.GetProperty("elapsedMs").GetDouble() == 0 ? ProcessTrafficState.Sampling : ProcessTrafficState.Partial;
        var rows = new List<ProcessTrafficRow>();
        if (state != ProcessTrafficState.Sampling)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in snapshot.GetProperty("processes").EnumerateArray())
            {
                var received = ReadRate(item, "rxBytesPerSec");
                var sent = ReadRate(item, "txBytesPerSec");
                var appRun = ReadAppRun(item);
                if (received is null && sent is null && appRun is null) continue;
                var process = TrafficProcessIdentity.Read(item);
                var row = new ProcessTrafficRow(process, received, sent, appRun, ReadAppGroup(item, process));
                if (!keys.Add(row.Key)) throw new InvalidDataException("Repeated traffic ranking identity.");
                rows.Add(row);
            }
        }
        return new(state, rows, Enabled: true);
    }

    static TrafficAppGroup? ReadAppGroup(JsonElement row, TrafficProcessIdentity representative)
    {
        if (!row.TryGetProperty("appRunMembers", out var members)) return null;
        var id = row.GetProperty("appRunId").GetString();
        var count = row.GetProperty("appRunMemberCount").GetInt32();
        if (!Guid.TryParseExact(id, "N", out _) || members.ValueKind != JsonValueKind.Array ||
            members.GetArrayLength() != Math.Min(count, 12) || count <= 0)
            throw new InvalidDataException("Invalid traffic application group.");
        var processes = members.EnumerateArray().Select(member => ReadAppMember(member, representative.ImagePath)).ToArray();
        var first = processes[0];
        if (first.InstanceId != representative.InstanceId || first.Pid != representative.Pid || first.Name != representative.Name ||
            processes.Select(process => process.InstanceId).Distinct(StringComparer.Ordinal).Count() != processes.Length)
            throw new InvalidDataException("Invalid traffic application group members.");
        processes[0] = representative;
        return new(id!, count, processes);
    }

    static TrafficProcessIdentity ReadAppMember(JsonElement member, string? groupImagePath)
    {
        var process = TrafficProcessIdentity.Read(member);
        if (groupImagePath == null) return process;
        var instance = process.InstanceId.AsSpan();
        var bootEnd = instance.IndexOf(':');
        var pidEnd = instance.LastIndexOf(':');
        if (bootEnd <= 0 || pidEnd <= bootEnd || !Guid.TryParseExact(instance[..bootEnd], "N", out _) ||
            !int.TryParse(instance[(bootEnd + 1)..pidEnd], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid != process.Pid ||
            !long.TryParse(instance[(pidEnd + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var created)) return process;
        return process with { CreatedFileTime = created, ImagePath = groupImagePath };
    }

    static double? ReadRate(JsonElement row, string property) => row.TryGetProperty(property, out var number) &&
        number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out var rate) && double.IsFinite(rate) && rate >= 0 ? rate : null;

    static TrafficAppRun? ReadAppRun(JsonElement row)
    {
        if (!row.TryGetProperty("appRunCoverage", out var coverage) || coverage.ValueKind != JsonValueKind.String ||
            coverage.GetString() is not ("complete" or "gap") ||
            !TryReadRunBytes(row, "appRunRxBytes", out var download) || !TryReadRunBytes(row, "appRunTxBytes", out var upload)) return null;
        return new(download, upload, coverage.GetString() == "gap");
    }

    static bool TryReadRunBytes(JsonElement row, string property, out long bytes)
    {
        bytes = 0;
        return row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out bytes);
    }

    ProcessTrafficSample Offline(ProcessTrafficState state)
    {
        Dispose();
        return offlineSample = new(state, []);
    }

    public void Dispose()
    {
        protocol?.Dispose();
        protocol = null;
        collectorEpoch = null;
    }
}
