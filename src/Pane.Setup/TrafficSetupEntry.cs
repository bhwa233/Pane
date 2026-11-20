using System.ComponentModel;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using Pane.Services.Traffic;

internal static class TrafficSetupEntry
{
    static int Main(string[] args)
    {
        var check = args is ["--check-first-install"];
        var install = args is ["--install-release"];
        var stage = install || args is ["--stage-release"];
        var release = check || stage || args is ["--verify-release"];
        if (!release && args is not ["--verify-payload"])
        {
            Console.Error.WriteLine("Usage: Pane.TrafficSetup.exe --verify-payload | --verify-release | --check-first-install | --stage-release | --install-release");
            return 2;
        }

        var phase = release ? "signature verification" : "payload verification";
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var trusted = false;
            if (release)
            {
                var signer = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .SingleOrDefault(attribute => attribute.Key == "TrafficReleaseSignerSha256")?.Value;
                trusted = Environment.ProcessPath is { } executable && TrafficReleaseTrust.Verify(executable, signer);
                if (!trusted && !check)
                {
                    Console.Error.WriteLine("Traffic setup refused: a trusted signature from the pinned release signer is required.");
                    return 3;
                }
            }
            SecurityIdentifier? user = null;
            if (stage || check)
            {
                phase = "administrator check";
                using var identity = WindowsIdentity.GetCurrent();
                var userSid = identity.User;
                var elevated = userSid != null && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                if (check)
                {
                    Console.WriteLine($"trustedReleaseSignature={trusted}; elevatedAdministrator={elevated}; account={identity.Name}");
                    user = elevated ? userSid : null;
                }
                else if (!elevated)
                {
                    Console.Error.WriteLine("Traffic setup refused: run the signed helper from an elevated Administrator terminal.");
                    return 4;
                }
                else user = userSid;
            }
            var installed = false;
            if (install || check)
            {
                phase = "existing service check";
                installed = TrafficServiceIdentity.IsInstalled();
                if (check) Console.WriteLine($"paneTrafficInstalled={installed}");
                else if (installed)
                {
                    Console.Error.WriteLine("Traffic setup refused: PaneTraffic is already installed; no changes were made.");
                    return 5;
                }
            }
            phase = "payload verification";
            var name = TrafficPreviewPayload.ArchiveName;
            using var archive = assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("Missing embedded traffic archive.");
            using var manifest = assembly.GetManifestResourceStream(name + ".manifest.json") ?? throw new InvalidDataException("Missing embedded traffic manifest.");
            using var verified = TrafficPreviewPayload.OpenVerified(archive, archive.Length, manifest, manifest.Length, out var digest);
            if (check)
            {
                var code = !trusted ? 3 : user == null ? 4 : installed ? 5 : 0;
                Console.WriteLine($"payloadSha256={digest}; firstInstallPrerequisitesMet={code == 0}");
                return code;
            }
            if (stage)
            {
                phase = "protected staging";
                var version = TrafficServiceStaging.Stage(verified, digest);
                if (install)
                {
                    phase = "service installation";
                    TrafficServiceInstall.Install(version, user!);
                }
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or UnauthorizedAccessException or System.Security.SecurityException or Win32Exception)
        {
            Console.Error.WriteLine($"Traffic setup failed during {phase}: {error.Message}");
            return 1;
        }
    }
}
