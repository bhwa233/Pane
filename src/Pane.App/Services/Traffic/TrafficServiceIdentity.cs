using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Pane.Services.Traffic;

internal static class TrafficServiceIdentity
{
    internal const string PipeName = "pane-traffic-v1";
    internal const string ServiceName = "PaneTraffic";
    internal const string ExecutableName = "Pane.TrafficService.exe";
    static readonly SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static bool IsInstalled()
    {
        using var manager = OpenSCManager(null, null, 1);
        using var service = Open(manager);
        return service != null;
    }

    internal static int? RunningProcessId(string serviceName)
    {
        using var manager = OpenSCManager(null, null, 1);
        using var service = Open(manager, serviceName);
        if (service == null) return null;
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _)) throw NativeError();
        return status.Type == 0x10 && status.State == 4 && status.ProcessId > 0 ? checked((int)status.ProcessId) : null;
    }

    public static void Verify(SafePipeHandle pipe, int? expectedProcessId = null)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var pid)) throw NativeError();
        using var manager = OpenSCManager(null, null, 1);
        using var service = Open(manager);
        if (service == null || !QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _) ||
            status.Type != 0x10 || status.State != 4 || status.ProcessId != pid)
            throw new IOException("The pipe is not served by the running Pane traffic service.");
        if (expectedProcessId is { } expected && pid != expected)
            throw new IOException("The running Pane traffic service does not match the sampled process.");

        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
        if (key == null) throw new IOException("The Pane traffic service has no configuration.");
        var registrySecurity = key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        VerifyOwner(registrySecurity);
        const RegistryRights writable = RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete |
            RegistryRights.ChangePermissions | RegistryRights.TakeOwnership;
        foreach (RegistryAccessRule rule in registrySecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 ||
                (rule.RegistryRights & writable) == 0) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (!IsAdministrator(sid) && !(sid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal) && sid.Value.Split('-').Length == 9))
                throw new IOException("The Pane traffic service configuration is writable by an untrusted principal.");
        }

        var account = key.GetValue("ObjectName", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var image = key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (!string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase) || image is not { Length: >= 3 } ||
            image[0] != '"' || image[^1] != '"' || image.AsSpan(1, image.Length - 2).Contains('"'))
            throw new IOException("The Pane traffic service executable configuration is not trusted.");

        var executable = image[1..^1];
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var root = Path.Combine(programData, "Pane", "traffic", "service") + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(executable) || !executable.Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) ||
            !executable.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !ExecutableName.Equals(Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Pane traffic service has an unexpected executable path.");

        var file = new FileInfo(executable);
        VerifyPath(file, file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
        for (var directory = file.Directory; directory != null && !directory.FullName.Equals(programData, StringComparison.OrdinalIgnoreCase); directory = directory.Parent)
            VerifyPath(directory, directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
    }

    static void VerifyPath(FileSystemInfo path, FileSystemSecurity security)
    {
        if ((path.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected service reparse point.");
        VerifyOwner(security);
        const FileSystemRights read = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !IsAdministrator((SecurityIdentifier)rule.IdentityReference) &&
                (rule.FileSystemRights & ~read) != 0)
                throw new IOException("The Pane traffic service code path is writable by an untrusted principal.");
    }

    static void VerifyOwner(ObjectSecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsAdministrator(owner))
            throw new IOException("Unexpected Pane traffic service owner.");
    }

    static bool IsAdministrator(SecurityIdentifier sid) => sid.Equals(system) || sid.Equals(administrators);

    static ServiceHandle? Open(ServiceHandle manager, string serviceName = ServiceName)
    {
        if (manager.IsInvalid) throw NativeError();
        var service = OpenService(manager, serviceName, 4);
        if (!service.IsInvalid) return service;
        var error = Marshal.GetLastPInvokeError();
        service.Dispose();
        if (error == 1060) return null;
        throw new Win32Exception(error);
    }

    static Win32Exception NativeError() => new(Marshal.GetLastPInvokeError());

    sealed class ServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatus
    {
        public uint Type, State, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatus status, int size, out int needed);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseServiceHandle(IntPtr handle);
}
