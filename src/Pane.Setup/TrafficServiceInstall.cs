using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Pane.Services.Traffic;
using Spark.Traffic;

internal static class TrafficServiceInstall
{
    internal static void Install(string versionDirectory, SecurityIdentifier user)
    {
        using var code = DirectoryGuard.OpenProtected(versionDirectory, true);
        var traffic = Path.GetDirectoryName(Path.GetDirectoryName(versionDirectory))!;
        using var root = DirectoryGuard.OpenProtected(traffic, true);
        var data = Path.Combine(traffic, "data");
        DirectoryGuard.CreateProtected(data, false);
        using var dataGuard = DirectoryGuard.OpenProtected(data, false);
        var grants = Path.Combine(data, "grants.json");
        if (File.Exists(grants))
        {
            using var input = DirectoryGuard.OpenPrivateFile(grants);
            if (input.Length > 64 * 1024) throw new InvalidDataException("Invalid traffic grant inventory.");
            using var existing = JsonDocument.Parse(input, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (existing.RootElement.ValueKind != JsonValueKind.Object || existing.RootElement.EnumerateObject().Any(entry =>
                entry.Value.ValueKind != JsonValueKind.False || new SecurityIdentifier(entry.Name).Value != entry.Name))
                throw new InvalidDataException("Traffic grants must only contain own-scope grants for Windows SIDs.");
            if (!existing.RootElement.TryGetProperty(user.Value, out _))
                throw new InvalidOperationException("Existing traffic grants do not include this account.");
        }

        var executable = Path.Combine(versionDirectory, TrafficServiceIdentity.ExecutableName);
        CheckSc("create", "binPath=", $"\"{executable}\"", "type=", "own", "start=", "demand", "obj=", "LocalSystem", "DisplayName=", "Pane Traffic Service");
        var wroteGrant = false;
        try
        {
            if (!File.Exists(grants))
            {
                using var output = DirectoryGuard.CreatePrivateFile(grants);
                wroteGrant = true;
                using var json = new Utf8JsonWriter(output);
                json.WriteStartObject();
                json.WriteBoolean(user.Value, false);
                json.WriteEndObject();
                json.Flush();
                output.Flush(flushToDisk: true);
            }
            CheckSc("start");
            WaitForPipeAsync().GetAwaiter().GetResult();
            CheckSc("config", "start=", "auto");
        }
        catch
        {
            _ = TrySc("stop");
            if (TrySc("delete") && wroteGrant)
            {
                try { File.Delete(grants); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    static async Task WaitForPipeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Exception? lastProbeError = null;
        try
        {
            while (true)
            {
                try
                {
                    using var pipe = await TrafficPipeProtocol.ConnectAsync(deadline.Token).ConfigureAwait(false);
                    var status = await pipe.RequestAsync("status", deadline.Token).ConfigureAwait(false);
                    if (!status.GetProperty("installed").GetBoolean() || !status.GetProperty("serviceAvailable").GetBoolean() ||
                        status.GetProperty("scope").GetString() != "own")
                        throw new InvalidDataException("Unexpected traffic service status.");
                    return;
                }
                catch (Exception error) when (error is TimeoutException or (IOException and not TrafficRequestException))
                {
                    lastProbeError = error;
                    await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            var reason = lastProbeError is null ? "" : $" Last pipe check: {lastProbeError.Message}";
            throw new IOException($"The Pane traffic service did not become ready within 10 seconds.{reason}", lastProbeError ?? error);
        }
    }

    static void CheckSc(string action, params string[] options)
    {
        var exit = RunSc(action, options);
        if (exit != 0) throw new IOException($"Traffic service {action} failed ({exit}).");
    }

    static bool TrySc(string action)
    {
        try { return RunSc(action) == 0; }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    static int RunSc(string action, params string[] options)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory
        };
        start.ArgumentList.Add(action);
        start.ArgumentList.Add(TrafficServiceIdentity.ServiceName);
        foreach (var option in options) start.ArgumentList.Add(option);
        using var process = Process.Start(start) ?? throw new IOException("Unable to start service control.");
        process.WaitForExit();
        return process.ExitCode;
    }
}
