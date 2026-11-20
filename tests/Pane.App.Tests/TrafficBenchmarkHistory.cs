using System.ComponentModel;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Pane.Services.Traffic;

static class TrafficBenchmarkHistory
{
    internal readonly record struct Load(int Pid, long CreatedFileTimeUtc, DateTimeOffset Start, DateTimeOffset End, long Bytes);

    internal static async Task<int> RunAsync(string report, string? galaxyBridge = null)
    {
        try
        {
            using var input = File.OpenRead(report);
            if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("Traffic counter report is too large.");
            using var document = await JsonDocument.ParseAsync(input);
            var product = galaxyBridge == null ? "Pane" : "Galaxy";
            var serviceName = galaxyBridge == null ? TrafficServiceIdentity.ServiceName : GalaxyTrafficBenchmarkBridge.ServiceName;
            var load = ReadLoad(document.RootElement, product);
            if (document.RootElement.TryGetProperty("accountSidSha256", out var accountHash))
            {
                using var account = WindowsIdentity.GetCurrent();
                if (account.User is not { } sid || accountHash.ValueKind != JsonValueKind.String ||
                    accountHash.GetString() != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid.Value))))
                    throw new InvalidDataException("Verify history under the same Windows account that sampled this report.");
            }
            var service = document.RootElement.GetProperty("registeredService");
            var sampledProcess = service.GetProperty("pid").GetInt32();
            var role = document.RootElement.GetProperty("roles").GetProperty("service");
            if (service.GetProperty("name").GetString() != serviceName || sampledProcess <= 0 ||
                role.GetArrayLength() != 1 || role[0].GetInt32() != sampledProcess)
                throw new InvalidDataException($"The report does not identify the registered {product} traffic service in the sampled counters.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            (string instance, decimal download, decimal upload) verified;
            if (galaxyBridge != null)
            {
                GalaxyTrafficBenchmarkBridge.VerifyService(sampledProcess);
                await using var bridge = new GalaxyTrafficBenchmarkBridge(galaxyBridge);
                await bridge.InitializeAsync(deadline.Token);
                verified = await VerifyAsync(bridge.ReadPageAsync, load, deadline.Token);
                GalaxyTrafficBenchmarkBridge.VerifyService(sampledProcess);
            }
            else
            {
                if (!TrafficServiceIdentity.IsInstalled())
                {
                    Console.Error.WriteLine("PaneTraffic is not installed; persisted history was not checked.");
                    return 3;
                }
                using var pipe = await TrafficPipeProtocol.ConnectAsync(deadline.Token, sampledProcess);
                verified = await VerifyAsync(pipe, load, deadline.Token);
            }
            input.Position = 0;
            var reportSha256 = Convert.ToHexStringLower(SHA256.HashData(input));
            using var output = new Utf8JsonWriter(Console.OpenStandardOutput());
            output.WriteStartObject();
            output.WriteBoolean("persistedHistoryVerified", true);
            output.WriteString("product", product);
            output.WriteString("reportSha256", reportSha256);
            output.WriteNumber("servicePid", sampledProcess);
            output.WriteNumber("pid", load.Pid);
            output.WriteString("processInstanceId", verified.instance);
            output.WriteString("startUtc", load.Start);
            output.WriteString("endUtc", load.End);
            output.WriteNumber("recordedRxBytes", verified.download);
            output.WriteNumber("recordedTxBytes", verified.upload);
            output.WriteEndObject();
            output.Flush();
            return 0;
        }
        catch (Exception error) when (ProbeFailure(error))
        {
            WriteError(error);
            return 1;
        }
    }

    internal static bool ProbeFailure(Exception error) => error is IOException or Win32Exception or SecurityException or UnauthorizedAccessException or
        InvalidDataException or InvalidOperationException or JsonException or KeyNotFoundException or ArgumentException or FormatException or OperationCanceledException;

    internal static void WriteError(Exception error) =>
        Console.Error.WriteLine(error is TrafficRequestException request ? $"Traffic request rejected: {request.Code}" : error.Message);

    internal static Load ReadLoad(JsonElement report, string product = "Pane")
    {
        if (report.GetProperty("product").GetString() != product || report.GetProperty("measurement").GetString() != "process-counters-only")
            throw new InvalidDataException($"Use a {product} traffic counter report.");
        var load = report.GetProperty("load");
        var pid = load.GetProperty("pid").GetInt32();
        var start = Milliseconds(load.GetProperty("startUtc").GetDateTimeOffset());
        var end = Milliseconds(load.GetProperty("endUtc").GetDateTimeOffset());
        var elapsed = load.GetProperty("elapsedMs").GetInt32();
        const int expected = 600 * 32 * 1024;
        if (pid <= 0 || elapsed is < 58_000 or > 63_000 || Math.Abs((end - start).TotalMilliseconds - elapsed) > 500 ||
            end > DateTimeOffset.UtcNow || start < DateTimeOffset.UtcNow.AddHours(-47) ||
            load.GetProperty("rxPayloadBytes").GetInt32() != expected || load.GetProperty("txPayloadBytes").GetInt32() != expected ||
            !long.TryParse(load.GetProperty("createdFileTimeUtc").GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var created) ||
            created <= 0 || created > start.ToFileTime() || start.ToFileTime() - created > TimeSpan.TicksPerSecond * 10)
            throw new InvalidDataException("The report does not identify a recent, complete fixed-load run.");
        return new(pid, created, start, end, expected);
    }

    internal static Task<(string Instance, decimal Download, decimal Upload)> VerifyAsync(TrafficPipeProtocol pipe, Load load, CancellationToken token) =>
        VerifyAsync((query, cursor, cancellation) => pipe.RequestAsync("history", cancellation, limit: 40, history: query, cursor: cursor), load, token, requireTotals: true);

    internal static async Task<(string Instance, decimal Download, decimal Upload)> VerifyAsync(
        Func<TrafficHistoryQuery, string?, CancellationToken, Task<JsonElement>> read, Load load, CancellationToken token, bool requireTotals = false)
    {
        var wholeMinute = load.Start.ToUnixTimeSeconds() / 60 * 60;
        var rows = new TrafficHistoryQuery(DateTimeOffset.FromUnixTimeSeconds(wholeMinute), load.End, 60);
        var suffix = FormattableString.Invariant($":{load.Pid}:{load.CreatedFileTimeUtc}");
        string? instance = null;
        string? cursor = null;
        async Task<(TrafficHistoryPage Page, DateTimeOffset CoverageStart)> QueryAsync(TrafficHistoryQuery query, string? next)
        {
            var raw = await read(query, next, token);
            if (raw.GetProperty("scope").GetString() != "own" || raw.GetProperty("source").GetString() != "etw" ||
                raw.GetProperty("resolutionSec").GetInt32() != query.ResolutionSeconds)
                throw new InvalidDataException("The history did not return own-scope ETW minute buckets.");
            var durable = raw.GetProperty("durableThrough");
            if (durable.ValueKind != JsonValueKind.String || durable.GetDateTimeOffset() < load.End)
                throw new InvalidDataException("History is not durable through the load interval. Check that collection remained active through a flush.");
            return (TrafficHistoryClient.ParsePage(raw, query), raw.GetProperty("coverageSummary").GetProperty("start").GetDateTimeOffset());
        }
        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            var (page, _) = await QueryAsync(rows, cursor);
            foreach (var row in page.Rows)
            {
                var id = row.Process.InstanceId;
                if (row.Process.Pid != load.Pid || !id.EndsWith(suffix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(id.AsSpan(0, id.Length - suffix.Length), "N", out _)) continue;
                if (instance != null && instance != id) throw new InvalidDataException("Load rows span multiple boot identities.");
                instance = id;
            }
            cursor = page.Cursor;
            if (cursor == null) break;
        }
        if (cursor != null || instance == null) throw new InvalidDataException("No unique load process instance was found in the bounded history scan.");
        var (exact, coveredFrom) = await QueryAsync(new(load.Start, load.End, 60, instance), null);
        if (exact.Coverage != TrafficHistoryCoverage.Complete || coveredFrom != load.Start || exact.CoveredThrough != load.End)
            throw new InvalidDataException("The exact load window was not fully covered by persisted collection.");
        var (attributed, _) = await QueryAsync(rows with { ProcessInstanceId = instance }, null);
        if (attributed.Cursor != null || attributed.Rows.Any(row => row.Process.Pid != load.Pid || row.Bucket < rows.From || row.Bucket >= rows.To ||
                row.Bucket.ToUnixTimeMilliseconds() % 60_000 != 0) || attributed.Rows.Select(row => row.Bucket).Distinct().Count() != attributed.Rows.Count)
            throw new InvalidDataException("Persisted traffic did not return the expected unique minute buckets for the load process.");
        var totals = new TrafficHistoryTotals(attributed.Rows.Sum(row => (decimal)row.Download), attributed.Rows.Sum(row => (decimal)row.Upload));
        if (attributed.Totals == null ? requireTotals : attributed.Totals != totals)
            throw new InvalidDataException("Reported history totals do not match the persisted minute buckets.");
        if (totals.Download < load.Bytes || totals.Upload < load.Bytes)
            throw new InvalidDataException("Persisted traffic for the load process is below the transmitted payload in at least one direction.");
        return (instance, totals.Download, totals.Upload);
    }

    static DateTimeOffset Milliseconds(DateTimeOffset time) => DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds());
}
