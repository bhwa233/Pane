using Pane.Localization;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
namespace Pane.Hosting;

internal static class RestartProcess
{
    const string ParentVariable = "PANE_RESTART_PARENT";
    static string? stagedUpdate;
    public static bool Requested { get; set; }

    public static void PreparePortableUpdate(string package, string sha256)
    {
        var launcher = RuntimeMode.PortableExecutable ?? throw new InvalidOperationException();
        var directory = Path.GetDirectoryName(launcher)!;
        var staging = Path.Combine(directory, $".pane-update-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(package, staging);
            using var stream = File.OpenRead(staging);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Strings.Update_InvalidDownload);
            stagedUpdate = staging;
            Requested = true;
        }
        finally
        {
            if (stagedUpdate != staging) File.Delete(staging);
        }
    }
    public static void WaitForParent()
    {
        var value = Environment.GetEnvironmentVariable(ParentVariable);
        Environment.SetEnvironmentVariable(ParentVariable, null);
        if (value == null) return;
        var parts = value.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int pid) || !long.TryParse(parts[1], out long ticks))
            throw new InvalidDataException("重启进程标识无效"); // 不翻译：异常
        Process parent;
        try { parent = Process.GetProcessById(pid); } catch (ArgumentException) { return; }
        using (parent) {
            try {
                if (parent.StartTime.ToUniversalTime().Ticks != ticks) return;
                if (!parent.WaitForExit(30000)) throw new TimeoutException("等待旧进程退出超时"); // 不翻译：异常
            } catch (InvalidOperationException) when (parent.HasExited) {
                // 查询启动时间前旧进程已退出，也可以继续获取单实例锁。
            }
        }
    }
    public static void Start()
    {
        var executable = RuntimeMode.LauncherExecutable;
        if (stagedUpdate is not { } update)
        {
            Start(executable);
            return;
        }
        var backup = executable + ".previous";
        try
        {
            // The running program is in the bundle cache. Replace its launcher on the same volume.
            File.Replace(update, executable, backup);
            try { Start(executable); }
            catch
            {
                File.Replace(backup, executable, null);
                throw;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBoxW(0, Strings.Update_InstallLaunchFailed + "\n" + error.Message, "Pane", 0x10);
            Start(executable);
            throw;
        }
        finally { DiscardUpdate(); }
    }

    internal static void DiscardUpdate()
    {
        if (stagedUpdate is not { } path) return;
        stagedUpdate = null;
        File.Delete(path);
    }

    static void Start(string executable)
    {
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1)) start.ArgumentList.Add(arg);
        start.Environment[ParentVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ":" + current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        using var child = Process.Start(start) ?? throw new IOException(Strings.Common_RestartFailed);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int MessageBoxW(nint window, string text, string caption, uint flags);
}
