using System.Diagnostics;
using System.Text.Json;
using Pane.Services.Traffic;

internal static class ProcessTrafficTests
{
    internal static void Run()
    {
        using var snapshot = JsonDocument.Parse("""
            {"quality":{"state":"partial"},"collecting":true,"elapsedMs":1000,"processes":[
              {"processInstanceId":"observed","pid":12,"name":"Example","rxBytesPerSec":null,"txBytesPerSec":null,
               "appRunRxBytes":"9007199254740993","appRunTxBytes":"0","appRunCoverage":"gap"},
              {"processInstanceId":"complete","pid":13,"name":"Other","rxBytesPerSec":1,"txBytesPerSec":0,
               "appRunRxBytes":"4096","appRunTxBytes":"1024","appRunCoverage":"complete"},
              {"processInstanceId":"invalid","pid":14,"name":"Bad data","rxBytesPerSec":1,"txBytesPerSec":0,
               "appRunRxBytes":"-1","appRunTxBytes":"0","appRunCoverage":"complete"},
              {"processInstanceId":"missing","pid":15,"name":"Missing counters","rxBytesPerSec":1,"txBytesPerSec":0}
            ]}
            """);
        var rows = ProcessTrafficClient.ParseSnapshot(snapshot.RootElement).Rows.ToDictionary(row => row.Process.InstanceId);
        var observed = rows["observed"];
        if (observed.Download != null || observed.Upload != null || observed.AppRun != new TrafficAppRun(9007199254740993, 0, true))
            throw new InvalidOperationException("A window with missing rates must retain the exact observed totals and coverage gap.");
        if (rows["complete"].AppRun != new TrafficAppRun(4096, 1024, false) || rows["complete"].Upload != 0)
            throw new InvalidOperationException("A complete app run and a measured zero rate must stay distinct from missing data.");
        if (rows["invalid"].AppRun != null || rows["missing"].AppRun != null)
            throw new InvalidOperationException("Invalid or missing totals must not appear as a measured zero.");

        using var denied = JsonDocument.Parse("""
            {"quality":{"state":"unavailable","reason":"etw-unavailable:UnauthorizedAccessException"},"collecting":false}
            """);
        var deniedSample = ProcessTrafficClient.ParseSnapshot(denied.RootElement);
        if (deniedSample.State != ProcessTrafficState.PermissionDenied || deniedSample.Enabled != true || deniedSample.Rows.Count != 0)
            throw new InvalidOperationException("An ETW permission failure must keep the persisted switch on without reporting measured data.");

        using var connection = JsonDocument.Parse("""
            {"quality":{"state":"unavailable","reason":"scope-inactive"},"collecting":false}
            """);
        if (ProcessTrafficClient.ParseSnapshot(connection.RootElement).State != ProcessTrafficState.Unavailable)
            throw new InvalidOperationException("An unavailable sampling scope must not be mistaken for an ETW permission failure.");
        CheckConnections();
        CheckCalendarHistory();
        CheckRankingOrder();
        CheckAppGroupIdentity();
        CheckProcessFileIdentity();
        CheckEndProcessIdentity();
    }

    static void CheckProcessFileIdentity()
    {
        using var process = Process.GetCurrentProcess();
        var path = Environment.ProcessPath!;
        var current = new TrafficProcessIdentity("fixture", process.ProcessName, process.Id, process.StartTime.ToFileTimeUtc(), path);
        if (!string.Equals(TrafficProcessFile.Resolve(current), path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The real process executable must be identified without opening the shell.");
        if (TrafficProcessFile.Resolve(current with { CreatedFileTime = current.CreatedFileTime - 1 }) != null ||
            TrafficProcessFile.Resolve(current with { ImagePath = Path.Combine(Path.GetDirectoryName(path)!, "different.exe") }) != null)
            throw new InvalidOperationException("A stale PID or a mismatched executable must not be passed to the shell.");
        if (TrafficProcessFile.HasTarget(current with { ImagePath = @"\\invalid\share\example.exe" }))
            throw new InvalidOperationException("A network path must not be passed to the shell.");
    }

    static void CheckEndProcessIdentity()
    {
        var launch = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        launch.ArgumentList.Add("--traffic-end-process-fixture");
        using var child = Process.Start(launch) ?? throw new InvalidOperationException("Unable to start the controlled traffic process.");
        try
        {
            var identity = new TrafficProcessIdentity("fixture-child", child.ProcessName, child.Id, child.StartTime.ToFileTimeUtc(), launch.FileName);
            if (TrafficProcessFile.TryEnd(identity with { CreatedFileTime = identity.CreatedFileTime - 1 }) ||
                TrafficProcessFile.TryEnd(identity with { ImagePath = Path.Combine(AppContext.BaseDirectory, "different.exe") }) || child.HasExited)
                throw new InvalidOperationException("The traffic action ended a process that did not match the selected instance.");
            using var current = Process.GetCurrentProcess();
            var self = identity with { Pid = current.Id, CreatedFileTime = current.StartTime.ToFileTimeUtc() };
            if (TrafficProcessFile.CanEnd(self)) throw new InvalidOperationException("The traffic action must refuse the host process.");
            if (!TrafficProcessFile.TryEnd(identity) || !child.WaitForExit(5000) || child.ExitCode != 1 || TrafficProcessFile.TryEnd(identity))
                throw new InvalidOperationException("The selected child must exit and the stale instance must then be unavailable.");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); }
        }
    }

    static void CheckAppGroupIdentity()
    {
        const string runId = "54a24257b76a4ba3bdb07e9bbaa178d5";
        const string first = "ff4090c39a9343d4874d68799b504770:14:134000000000000001";
        const string second = "ff4090c39a9343d4874d68799b504770:15:134000000000000002";
        const string withGroupPath = "\"imagePath\":\"C:\\\\Example\\\\browser.exe\",";
        var payload = $$"""
            {"quality":{"state":"good"},"collecting":true,"processes":[
              {"processInstanceId":"{{first}}","pid":14,"name":"Browser",{{withGroupPath}}
               "createdFileTime":"134000000000000001","appRunId":"{{runId}}","appRunRxBytes":"9007199254740993",
               "appRunTxBytes":"0","appRunCoverage":"gap","appRunMemberCount":2,"appRunMembers":[
                 {"processInstanceId":"{{first}}","pid":14,"name":"Browser"},{"processInstanceId":"{{second}}","pid":15,"name":"Browser Worker"}]},
              {"processInstanceId":"other","pid":16,"name":"Other","rxBytesPerSec":100}
            ]}
            """;
        using var grouped = JsonDocument.Parse(payload);
        var rows = ProcessTrafficClient.ParseSnapshot(grouped.RootElement).Rows;
        var result = TrafficRankingOrder.Preserve(rows.Reverse(), [$"app:{runId}", "other"]).ToArray();
        if (result[0].AppRun != new TrafficAppRun(9007199254740993, 0, true) ||
            result[0].AppGroup is not { MemberCount: 2 } group ||
            !group.Members.Select(member => member.InstanceId).SequenceEqual([first, second]) ||
            group.Members[1] is not { ImagePath: @"C:\Example\browser.exe", CreatedFileTime: 134000000000000002 } ||
            result[1].Process.InstanceId != "other")
            throw new InvalidOperationException("An application row must keep its rank across representative changes and retain concrete targets with exact totals.");
        using var missingPath = JsonDocument.Parse(payload.Replace(withGroupPath, string.Empty, StringComparison.Ordinal));
        var unresolved = ProcessTrafficClient.ParseSnapshot(missingPath.RootElement).Rows[0];
        if (unresolved.AppGroup is not { MemberCount: 2 } unknown || unknown.Members.Any(TrafficProcessFile.HasTarget))
            throw new InvalidOperationException("Missing image paths must only disable file actions, not discard the ranking group.");
    }

    static void CheckRankingOrder()
    {
        var restarted = new ProcessTrafficRow(new("new-instance", "Browser", 12), 900, 4, null);
        var download = new ProcessTrafficRow(new("download", "Download", 13), 500, 0, null);
        var upload = new ProcessTrafficRow(new("upload", "Upload", 14), 200, 300, null);
        var rendered = TrafficRankingOrder.Preserve([restarted, download, upload], ["upload", "exited-instance", "download"]);
        if (!rendered.SequenceEqual([upload, download, restarted]))
            throw new InvalidOperationException("Keeping the ranking order must render the new counters, preserve surviving instances, and append a restarted process even when it has the same PID.");

        using var repeated = JsonDocument.Parse("""
            {"quality":{"state":"good"},"collecting":true,"processes":[
              {"processInstanceId":"same","pid":12,"name":"Old","rxBytesPerSec":1},
              {"processInstanceId":"same","pid":13,"name":"New","rxBytesPerSec":2}
            ]}
            """);
        try { ProcessTrafficClient.ParseSnapshot(repeated.RootElement); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("A repeated instance ID must not reassign a ranking row to another process.");
    }

    static void CheckCalendarHistory()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var day = new DateOnly(2025, 3, 9);
        var now = new DateTimeOffset(2025, 3, 10, 12, 0, 0, TimeSpan.Zero);
        var daylightChange = TrafficHistoryClient.CalendarQuery(day, day, now, eastern, "instance", out var expanded);
        if (daylightChange != new TrafficHistoryQuery(new(2025, 3, 9, 5, 0, 0, TimeSpan.Zero), new(2025, 3, 10, 4, 0, 0, TimeSpan.Zero), 60, "instance") || expanded)
            throw new InvalidOperationException("The local day with the DST transition must use its two actual midnights and retain the process filter.");
        var today = day.AddDays(1);
        if (TrafficHistoryClient.CalendarQuery(today, today, now, eastern, null, out _) is not { } current || current.To != now)
            throw new InvalidOperationException("A range ending today must stop at the query time, not request future history.");
        if (TrafficHistoryClient.CalendarQuery(today, day, now, eastern, null, out _) != null ||
            TrafficHistoryClient.CalendarQuery(today, today.AddDays(1), now, eastern, null, out _) != null ||
            TrafficHistoryClient.CalendarQuery(today.AddDays(-730), today, now, eastern, null, out _) != null)
            throw new InvalidOperationException("Reversed, future and unsupported ranges must not produce a service request.");
        var india = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var past = new DateOnly(2024, 11, 4);
        var old = TrafficHistoryClient.CalendarQuery(past, past, now, india, null, out expanded);
        if (old != new TrafficHistoryQuery(new(2024, 11, 3, 0, 0, 0, TimeSpan.Zero), new(2024, 11, 4, 18, 30, 0, TimeSpan.Zero), 86400) || !expanded)
            throw new InvalidOperationException("Daily UTC buckets overlapping a local date must be included and identified as approximate.");
    }

    internal static void CheckConnections()
    {
        using var payload = JsonDocument.Parse("""
            {"source":"ip-helper","scope":"own","kind":"tcp","processInstanceId":"fixture","truncated":false,"endpoints":[
              {"pid":192,"protocol":"tcp","addressFamily":"ipv6","state":"Established",
               "localAddress":"::1","localPort":52222,"remoteAddress":"::1","remotePort":443},
              {"pid":192,"protocol":"tcp","addressFamily":"ipv4","state":"Listen",
               "localAddress":"127.0.0.1","localPort":54656,"remoteAddress":"0.0.0.0","remotePort":0}
            ]}
            """);
        var process = new TrafficProcessIdentity("fixture", "Fixture", 192);
        var result = TrafficConnectionsClient.Parse(payload.RootElement, process, TrafficConnectionsClient.Tcp);
        if (result.Rows[0].Local != "[::1]:52222" || result.Rows[0].Remote != "[::1]:443" || result.Rows[1].Remote != null)
            throw new InvalidOperationException("IPv6 ports must be unambiguous and a listening socket must not display a peer.");
        CheckRejected(process with { InstanceId = "different" });
        CheckRejected(process with { Pid = 193 });

        void CheckRejected(TrafficProcessIdentity expected)
        {
            try { TrafficConnectionsClient.Parse(payload.RootElement, expected, TrafficConnectionsClient.Tcp); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("A response for a different process must not produce endpoints.");
        }
    }
}
