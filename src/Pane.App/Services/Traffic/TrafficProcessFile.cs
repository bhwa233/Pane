using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pane.Services.Traffic;

internal static class TrafficProcessFile
{
    const uint QueryLimitedInformation = 0x1000;
    const uint TerminateAccess = 0x0001, SynchronizeAccess = 0x00100000, TokenQuery = 0x0008, WaitTimeout = 258;

    internal static bool HasTarget(TrafficProcessIdentity process) => process.Pid > 0 && process.CreatedFileTime > 0 &&
        process.ImagePath is { Length: > 3 } path && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' &&
        !path.AsSpan(2).Contains(':') && !path.Any(char.IsControl);

    internal static string? Resolve(TrafficProcessIdentity process)
    {
        if (!HasTarget(process)) return null;
        using var handle = OpenProcess(QueryLimitedInformation, false, process.Pid);
        if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out var exited, out _, out _) ||
            created != process.CreatedFileTime || exited != 0) return null;
        var actual = MatchImage(handle, process.ImagePath!);
        return actual != null && File.Exists(actual) &&
            GetProcessTimes(handle, out _, out exited, out _, out _) && exited == 0 ? actual : null;
    }

    internal static bool CanEnd(TrafficProcessIdentity process) => HasTarget(process) && process.Pid != Environment.ProcessId &&
        !string.Equals(Path.GetFileName(process.ImagePath), TrafficServiceIdentity.ExecutableName, StringComparison.OrdinalIgnoreCase);

    internal static bool TryEnd(TrafficProcessIdentity process)
    {
        if (!CanEnd(process)) return false;
        using var handle = OpenProcess(QueryLimitedInformation | TerminateAccess | SynchronizeAccess, false, process.Pid);
        if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out _, out _, out _) || created != process.CreatedFileTime ||
            MatchImage(handle, process.ImagePath!) == null || !OpenProcessToken(handle, TokenQuery, out var token)) return false;
        using (token)
        using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
        {
            return owner.User is { } sid && current.User is { } currentSid && sid.Equals(currentSid) && WaitForSingleObject(handle, 0) == WaitTimeout &&
                IsProcessCritical(handle, out var critical) && !critical && TerminateProcess(handle, 1);
        }
    }

    static string? MatchImage(SafeProcessHandle handle, string expected)
    {
        var path = new StringBuilder(32768);
        int length = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref length)) return null;
        var actual = path.ToString();
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ? actual : null;
    }

    [DllImport("kernel32.dll")]
    static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll")]
    static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);

    [DllImport("advapi32.dll")]
    static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [DllImport("kernel32.dll")]
    static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll")]
    static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
