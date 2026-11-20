using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Pane.Services.Screenshots;

internal sealed class NativeScreenshotCapture(ScreenshotStore store, string workDirectory, Func<Guid, bool> pin, Func<Guid, bool> unpin, SemaphoreSlim pinSync)
{
    static readonly string executable = Path.Combine(AppContext.BaseDirectory, "NativeCapture", "ScreenCapture.exe");
    internal static bool Available => File.Exists(executable);
    Task? active;

    internal Task<(Guid Id, bool Recognize)?> CaptureAsync(CancellationToken token) => RunAsync(null, token);
    internal async Task<Guid?> EditAsync(Guid id, CancellationToken token) => (await RunAsync(id, token))?.Id;

    internal bool IsRunning => active is { IsCompleted: false };
    internal Task DrainAsync() => active?.WaitAsync(TimeSpan.FromSeconds(5)) ?? Task.CompletedTask;

    async Task<(Guid Id, bool Recognize)?> RunAsync(Guid? editId, CancellationToken token)
    {
        if (IsRunning) throw new InvalidOperationException("A capture session is already active.");
        if (!Available) throw new FileNotFoundException("Native screenshot tool is not installed.");
        token.ThrowIfCancellationRequested();
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        active = drained.Task;
        var session = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(workDirectory, session);
        try
        {
            Directory.CreateDirectory(directory);
            var launch = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, CreateNoWindow = true };
            launch.ArgumentList.Add($"--spark233-session={session}");
            launch.ArgumentList.Add($"--spark233-session-dir={directory}");
            launch.ArgumentList.Add(FormattableString.Invariant($"--spark233-capture-max-side={ScreenshotStore.MaxDimension}"));
            launch.ArgumentList.Add(FormattableString.Invariant($"--spark233-capture-max-pixels={ScreenshotStore.MaxPixels}"));
            var allowOcr = editId == null;
            launch.ArgumentList.Add(allowOcr ? "--spark233-ocr-enabled=true" : "--spark233-ocr-enabled=false");
            launch.ArgumentList.Add("--spark233-pin-host=true");
            launch.ArgumentList.Add($"--spark233-pin-owner-pid={Environment.ProcessId}");
            launch.ArgumentList.Add("--auto-quit=true");
            if (editId is { } id)
            {
                await Task.Run(() => store.PrepareEdit(id, directory), token);
                token.ThrowIfCancellationRequested();
                launch.ArgumentList.Add("--spark233-edit-pin=true");
            }
            using var process = Process.Start(launch) ?? throw new Win32Exception("Native screenshot tool did not start.");
            var pid = process.Id;
            using var request = token.Register(() => RequestCancel(directory, session, pid));
            var exited = process.WaitForExitAsync();
            Guid? accepted = null;
            try
            {
                var attempt = 0;
                while (!exited.IsCompleted && !token.IsCancellationRequested && accepted == null)
                {
                    if (await Task.WhenAny(exited, Task.Delay(100)) == exited || token.IsCancellationRequested) break;
                    if (ReadPin(directory, session, pid, attempt) is not { } pending) continue;
                    attempt = pending.Attempt;
                    await pinSync.WaitAsync(token);
                    try { accepted = await HandlePinAsync(directory, session, process, pending, editId, token); }
                    finally { pinSync.Release(); }
                }
                if (accepted == null) await exited.WaitAsync(token);
                else
                {
                    try { await exited.WaitAsync(TimeSpan.FromSeconds(3), token); }
                    catch (Exception error) when (error is OperationCanceledException or TimeoutException)
                    { await StopAsync(process, exited, directory, session); }
                }
            }
            catch
            {
                await StopAsync(process, exited, directory, session);
                throw;
            }
            if (accepted is { } savedId) return (savedId, false);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("Native screenshot tool did not exit normally.");
            if (editId != null) return null;
            if (ReadSelection(directory, session, pid, allowOcr) is not { } selection) return null;
            var imageId = await Task.Run(() => store.Import(Path.Combine(directory, "selection.png"), (selection.Width, selection.Height)), token);
            return (imageId, selection.Recognize);
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                {
                    foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
                    Directory.Delete(directory);
                }
            }
            catch (Exception error) when (ScreenshotStore.IsStorageError(error)) { }
            finally { drained.SetResult(); }
        }
    }

    static void RequestCancel(string directory, string session, int pid)
    {
        try { File.WriteAllText(Path.Combine(directory, "cancel.request"), $"{session}:{pid}:cancel"); }
        catch (Exception error) when (ScreenshotStore.IsStorageError(error)) { }
    }

    internal static async Task StopAsync(Process process, Task exited, string directory, string session)
    {
        if (exited.IsCompleted) return;
        RequestCancel(directory, session, process.Id);
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) == exited) return;
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) when (process.HasExited) { }
        await exited.ConfigureAwait(false);
    }

    readonly record struct PinRequest(int Attempt, (uint Width, uint Height)? Size);

    static PinRequest? ReadPin(string directory, string session, int pid, int previousAttempt)
    {
        if (!File.Exists(Path.Combine(directory, "pin.json"))) return null;
        using var stream = ScreenshotStore.OpenSessionFile(directory, "pin.json", 8192, FileShare.Read | FileShare.Delete);
        using var json = JsonDocument.Parse(stream);
        var data = json.RootElement;
        if (!MatchesSession(data, session, pid, "pin")) throw new InvalidDataException("Native pin does not match the active session.");
        var attempt = data.GetProperty("attempt").GetInt32();
        if (attempt is < 1 or > 1_000_000) throw new InvalidDataException("Invalid native pin attempt.");
        if (attempt <= previousAttempt) return null;
        if (data.GetProperty("action").GetString() != "pin" || data.GetProperty("image").GetString() != "pin.png" ||
            data.GetProperty("source").GetString() != "pin-source.png" || data.GetProperty("document").GetString() != "pin-document.json")
            return new(attempt, null);
        return new(attempt, (data.GetProperty("width").GetUInt32(), data.GetProperty("height").GetUInt32()));
    }

    async Task<Guid?> HandlePinAsync(string directory, string session, Process process, PinRequest request, Guid? editId, CancellationToken token)
    {
        Guid? id = null;
        ScreenshotEdit? edit = null;
        var accepted = false;
        try
        {
            var saved = false;
            try
            {
                if (request.Size is { } size)
                {
                    if (editId is { } existing)
                    {
                        edit = await Task.Run(() => store.StageEdit(existing, directory, size), token);
                        if (!token.IsCancellationRequested && !process.HasExited)
                        {
                            store.PublishEdit(edit.Value);
                            saved = true;
                        }
                    }
                    else
                    {
                        id = await Task.Run(() => store.ImportEditable(directory, size), token);
                        if (!token.IsCancellationRequested && !process.HasExited) saved = pin(id.Value);
                    }
                }
            }
            catch (Exception error) when (ScreenshotStore.IsStorageError(error) ||
                error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or
                    System.Runtime.InteropServices.COMException or Win32Exception) { }
            if (token.IsCancellationRequested || process.HasExited) return null;
            if (!saved)
            {
                RollBackPin(id);
                id = null;
            }
            var result = FormattableString.Invariant($"{session}:{process.Id}:{request.Attempt}:{(saved ? "accepted" : "rejected")}");
            var ack = Path.Combine(directory, FormattableString.Invariant($"pin-{request.Attempt}.ack"));
            var temporary = ack + ".tmp";
            File.WriteAllText(temporary, result);
            if (token.IsCancellationRequested || process.HasExited) return null;
            File.Move(temporary, ack);
            accepted = saved;
            return accepted ? editId ?? id : null;
        }
        finally
        {
            if (edit is { } revision) store.FinishEdit(revision, accepted);
            if (!accepted) RollBackPin(id);
        }
    }

    void RollBackPin(Guid? id)
    {
        if (id is not { } imageId) return;
        try { if (unpin(imageId)) store.DiscardEditable(imageId); }
        catch (Exception error) when (ScreenshotStore.IsStorageError(error)) { }
    }

    internal static bool MatchesSession(JsonElement data, string session, int pid, string kind) =>
        data.GetProperty("protocol").GetInt32() == 1 && data.GetProperty("pid").GetInt32() == pid &&
        data.GetProperty("sessionId").GetString() == session && data.GetProperty("kind").GetString() == kind;

    internal static (uint Width, uint Height, bool Recognize)? ReadSelection(string directory, string session, int pid, bool allowOcr)
    {
        var result = new FileInfo(Path.Combine(directory, "capture.json"));
        if (!result.Exists) return null;
        using var stream = ScreenshotStore.OpenSessionFile(directory, "capture.json", 8192);
        using var json = JsonDocument.Parse(stream);
        var data = json.RootElement;
        var action = data.GetProperty("action").GetString();
        var recognize = action == "ocr";
        if (!MatchesSession(data, session, pid, "capture") ||
            (action is not ("copy" or "save") && !(allowOcr && recognize)) || data.GetProperty("image").GetString() != "selection.png")
            throw new InvalidDataException("Native screenshot result does not match the active session.");
        var image = new FileInfo(Path.Combine(directory, "selection.png"));
        if (!image.Exists || (image.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid native screenshot image file.");
        return (data.GetProperty("width").GetUInt32(), data.GetProperty("height").GetUInt32(), recognize);
    }
}
