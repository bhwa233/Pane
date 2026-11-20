using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Pane.Services.Traffic;

sealed class GalaxyTrafficBenchmarkBridge : IAsyncDisposable
{
    internal const string ServiceName = "spark233Traffic";
    const string Owner = "pane-traffic-benchmark";
    readonly string session = Guid.NewGuid().ToString("N");
    readonly Process process;

    internal GalaxyTrafficBenchmarkBridge(string path)
    {
        var file = Path.GetFullPath(path);
        if (!Path.GetFileName(file).Equals("spark233.TrafficClient.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            throw new InvalidDataException("Use the native spark233.TrafficClient.exe from the sampled Galaxy installation.");
        var launch = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true)
        };
        launch.ArgumentList.Add("--bridge");
        process = Process.Start(launch) ?? throw new IOException("The Galaxy traffic client did not start.");
    }

    internal async Task InitializeAsync(CancellationToken token)
    {
        var ready = await SendAsync("init", null, null, null, token);
        if (!ready.GetProperty("ready").GetBoolean() || ready.GetProperty("pid").GetInt32() != process.Id)
            throw new InvalidDataException("The Galaxy bridge handshake did not match the child process.");
    }

    internal Task<JsonElement> ReadPageAsync(TrafficHistoryQuery query, string? cursor, CancellationToken token) =>
        SendAsync("query", "history", query, cursor, token);

    async Task<JsonElement> SendAsync(string type, string? command, TrafficHistoryQuery? query, string? cursor, CancellationToken token)
    {
        var request = Guid.NewGuid().ToString("N");
        using var message = new MemoryStream();
        using (var writer = new Utf8JsonWriter(message))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocol", 1);
            writer.WriteString("type", type);
            writer.WriteString("sessionId", session);
            writer.WriteString("requestId", request);
            writer.WriteStartObject("owner");
            writer.WriteString("id", Owner);
            writer.WriteEndObject();
            if (command != null)
            {
                writer.WriteStartObject("activation");
                writer.WriteStartObject("parameters");
                writer.WriteString("command", command);
                writer.WriteString("scope", "own");
                if (query is { } range)
                {
                    writer.WriteString("from", range.From);
                    writer.WriteString("to", range.To);
                    writer.WriteNumber("resolutionSec", range.ResolutionSeconds);
                    writer.WriteNumber("limit", 40);
                    if (range.ProcessInstanceId is { } instance) writer.WriteString("processInstanceId", instance);
                    if (cursor != null) writer.WriteString("cursor", cursor);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
        var line = await process.StandardOutput.ReadLineAsync(token);
        if (line is not { Length: > 0 and <= 128 * 1024 }) throw new InvalidDataException("Missing or oversized Galaxy bridge reply.");
        using var response = JsonDocument.Parse(line);
        var root = response.RootElement;
        if (root.GetProperty("protocol").GetInt32() != 1 || root.GetProperty("type").GetString() != "response" ||
            root.GetProperty("sessionId").GetString() != session || root.GetProperty("requestId").GetString() != request ||
            root.GetProperty("owner").GetProperty("id").GetString() != Owner)
            throw new InvalidDataException("Unmatched Galaxy bridge reply.");
        if (!root.GetProperty("ok").GetBoolean()) throw new TrafficRequestException(root.GetProperty("error").GetString());
        return root.GetProperty("value").Clone();
    }

    internal static void VerifyService(int expectedPid)
    {
        if (TrafficServiceIdentity.RunningProcessId(ServiceName) != expectedPid)
            throw new IOException("The running Galaxy traffic service does not match the sampled process.");
    }

    internal static async Task<int> RunStatusAsync(string path, int expectedPid)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            VerifyService(expectedPid);
            await using var bridge = new GalaxyTrafficBenchmarkBridge(path);
            await bridge.InitializeAsync(deadline.Token);
            var status = await bridge.SendAsync("query", "status", null, null, deadline.Token);
            var observed = DateTimeOffset.UtcNow;
            VerifyService(expectedPid);
            using var output = new Utf8JsonWriter(Console.OpenStandardOutput());
            output.WriteStartObject();
            output.WriteNumber("servicePid", expectedPid);
            output.WriteString("observedUtc", observed);
            output.WritePropertyName("status");
            status.WriteTo(output);
            output.WriteEndObject();
            output.Flush();
            return 0;
        }
        catch (Exception error) when (TrafficBenchmarkHistory.ProbeFailure(error))
        {
            TrafficBenchmarkHistory.WriteError(error);
            return 1;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (process)
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Win32Exception) when (process.HasExited) { }
                await process.WaitForExitAsync();
            }
        }
    }
}
