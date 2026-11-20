using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Pane.Services.Traffic;

static class TrafficServiceProbe
{
    const int ChunkBytes = 32 * 1024;

    internal static async Task<int> RunAsync(bool capture, int? statusProcessId = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = deadline.Token;
        try
        {
            if (!TrafficServiceIdentity.IsInstalled())
            {
                Console.Error.WriteLine("PaneTraffic is not installed; no collection was attempted.");
                return 3;
            }
            using var pipe = await TrafficPipeProtocol.ConnectAsync(token, statusProcessId);
            var status = await pipe.RequestAsync("status", token);
            Require(status.GetProperty("installed").GetBoolean() && status.GetProperty("serviceAvailable").GetBoolean() &&
                status.GetProperty("scope").GetString() == "own" && !status.GetProperty("hasLease").GetBoolean(),
                "The verified service did not return a read-only own status.");
            var enabled = status.GetProperty("enabled").GetBoolean();
            if (statusProcessId is { } pid)
            {
                using var writer = new Utf8JsonWriter(Console.OpenStandardOutput());
                writer.WriteStartObject();
                writer.WriteNumber("servicePid", pid);
                writer.WriteString("observedUtc", DateTimeOffset.UtcNow);
                writer.WritePropertyName("status");
                status.WriteTo(writer);
                writer.WriteEndObject();
                writer.Flush();
                return 0;
            }
            Console.WriteLine($"PASS traffic service readiness: verified SCM/pipe identity and own status; enabled={enabled}; no lease acquired.");
            if (!capture) return 0;
            Require(!enabled && !status.GetProperty("collecting").GetBoolean(),
                "Active capture requires an idle, dedicated test account with collection disabled.");
            await CaptureAsync(pipe, token);
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception or TimeoutException or UnauthorizedAccessException or
            System.Security.SecurityException or SocketException or JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine(error is TrafficRequestException request ? $"Traffic request rejected: {request.Code}" : error.Message);
            return 1;
        }
    }

    internal static async Task<int> RunLoopbackLoadAsync(bool json)
    {
        const int chunks = 600;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var token = deadline.Token;
        try
        {
            using var sender = new TcpClient(AddressFamily.InterNetwork);
            using var receiver = await ConnectLoopbackAsync(sender, token);
            using var output = sender.GetStream();
            using var input = receiver.GetStream();
            var send = new byte[ChunkBytes];
            var receive = new byte[ChunkBytes];
            var startedUtc = DateTimeOffset.UtcNow;
            if (!json) Console.WriteLine($"START loopback load: pid={Environment.ProcessId}; utc={startedUtc:O}.");
            var watch = Stopwatch.StartNew();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            for (var sample = 0; sample < chunks; sample++)
            {
                await timer.WaitForNextTickAsync(token);
                await TransferAsync(output, input, send, receive, token);
            }
            var elapsed = watch.ElapsedMilliseconds;
            var endedUtc = DateTimeOffset.UtcNow;
            Require(elapsed is >= 58_000 and <= 63_000, $"Loopback load took {elapsed} ms; reject this timing sample.");
            if (json)
            {
                using var writer = new Utf8JsonWriter(Console.OpenStandardOutput());
                writer.WriteStartObject();
                writer.WriteNumber("pid", Environment.ProcessId);
                writer.WriteString("startUtc", startedUtc);
                writer.WriteString("endUtc", endedUtc);
                writer.WriteNumber("elapsedMs", elapsed);
                writer.WriteNumber("txPayloadBytes", chunks * ChunkBytes);
                writer.WriteNumber("rxPayloadBytes", chunks * ChunkBytes);
                writer.WriteEndObject();
                writer.Flush();
            }
            else Console.WriteLine($"PASS loopback load: pid={Environment.ProcessId}; startUtc={startedUtc:O}; endUtc={endedUtc:O}; elapsedMs={elapsed}; txPayloadBytes={chunks * ChunkBytes}; rxPayloadBytes={chunks * ChunkBytes}.");
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or SocketException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Loopback load failed: {error.Message}");
            return 1;
        }
    }

    static async Task CaptureAsync(TrafficPipeProtocol pipe, CancellationToken token)
    {
        using var sender = new TcpClient(AddressFamily.InterNetwork);
        using var receiver = await ConnectLoopbackAsync(sender, token);
        using var output = sender.GetStream();
        using var input = receiver.GetStream();
        var from = DateTimeOffset.UtcNow;
        from = from.AddTicks(-(from.Ticks % TimeSpan.TicksPerMinute));
        TrafficProcessIdentity? observed = null;
        var quality = "no snapshot";
        try
        {
            await pipe.RequestAsync("scope.setEnabled", token, enabled: true);
            var lease = await pipe.RequestAsync("lease.acquire", token);
            var generation = lease.GetProperty("generation").GetInt32();
            var epoch = lease.GetProperty("collectorEpoch").GetString();
            var lastRenew = Stopwatch.GetTimestamp();
            var send = new byte[ChunkBytes];
            var receive = new byte[send.Length];
            for (var sample = 0; sample < 40 && observed == null; sample++)
            {
                if (Stopwatch.GetElapsedTime(lastRenew) >= TimeSpan.FromSeconds(4))
                {
                    await pipe.RequestAsync("lease.renew", token, generation: generation, collectorEpoch: epoch);
                    lastRenew = Stopwatch.GetTimestamp();
                }
                await TransferAsync(output, input, send, receive, token);
                await Task.Delay(600, token);
                var snapshot = await pipe.RequestAsync("snapshot", token, limit: 60);
                var parsed = ProcessTrafficClient.ParseSnapshot(snapshot);
                var serviceQuality = snapshot.GetProperty("quality");
                quality = serviceQuality.GetProperty("reason").GetString() ?? serviceQuality.GetProperty("state").GetString() ?? "unknown";
                Require(parsed.State != ProcessTrafficState.PermissionDenied, $"ETW did not start: {quality}.");
                observed = parsed.Rows.FirstOrDefault(row => row.Process.Pid == Environment.ProcessId &&
                    row.AppRun is { Download: > 0, Upload: > 0 })?.Process;
            }
            Require(observed != null, $"No observable send and receive bytes for probe PID {Environment.ProcessId}; last quality: {quality}.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var connection = await TrafficPipeProtocol.ConnectAsync(cleanup.Token);
            var disabled = await connection.RequestAsync("scope.setEnabled", cleanup.Token, enabled: false);
            Require(!disabled.GetProperty("enabled").GetBoolean(), "Restore the test account's disabled capture setting manually.");
        }

        var history = await TrafficHistoryClient.ReadAsync(new(from, DateTimeOffset.UtcNow, 60, observed!.InstanceId), null, token);
        Require(history.State == TrafficHistoryState.Available && history.Rows.Any(row => row.Process.Pid == observed.Pid) &&
            history.Totals is { Download: > 0, Upload: > 0 }, "No durable bidirectional traffic for the observed process instance.");
        Console.WriteLine($"PASS ETW attribution and persisted own history: pid={observed.Pid}; rx={history.Totals?.Download}; tx={history.Totals?.Upload}; liveQuality={quality}; historyCoverage={history.Coverage}.");
    }

    static async Task<TcpClient> ConnectLoopbackAsync(TcpClient sender, CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await sender.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
            return await listener.AcceptTcpClientAsync(token);
        }
        finally { listener.Stop(); }
    }

    static async Task TransferAsync(NetworkStream output, NetworkStream input, byte[] send, byte[] receive, CancellationToken token)
    {
        await output.WriteAsync(send, token);
        await input.ReadExactlyAsync(receive, token);
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
