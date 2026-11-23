using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Pane.Hosting;

namespace Pane.Services.Traffic;

internal static class TrafficServiceSetup
{
    internal enum Result { Installed, PackageUnavailable, Cancelled, DifferentAccount, Failed }
    internal static Task<Result>? Pending { get; private set; }

    internal static Task<Result> InstallAsync()
    {
        if (Pending is { IsCompleted: false } active) return active;
        return Pending = InstallCoreAsync();
    }

    static async Task<Result> InstallCoreAsync()
    {
        if (!RuntimeMode.PortableTraffic || RuntimeMode.PortableExecutable is not { } launcher)
            return Result.PackageUnavailable;
        // Yield to the UI so callers can publish the pending task before the shell displays UAC.
        await Task.Yield();
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not { } user) return Result.Failed;
            using var lockedLauncher = File.Open(launcher, FileMode.Open, FileAccess.Read, FileShare.Read);
            var start = new ProcessStartInfo(launcher) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.SystemDirectory };
            start.ArgumentList.Add("--install-traffic-service");
            start.ArgumentList.Add(user.Value);
            using var process = Process.Start(start);
            if (process == null) return Result.Failed;
            await process.WaitForExitAsync();
            return process.ExitCode switch { 0 => Result.Installed, 6 => Result.DifferentAccount, _ => Result.Failed };
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return Result.Cancelled; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return Result.Failed;
        }
    }
}
