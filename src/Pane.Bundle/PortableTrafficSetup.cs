using System.ComponentModel;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using Pane.Services.Traffic;

internal static class PortableTrafficSetup
{
    internal static int Run(string? requestedSid)
    {
        try
        {
            SecurityIdentifier? user = null;
            if (requestedSid != null)
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 4;
                // The service grants access to the interactive Pane user. Over-the-shoulder
                // elevation would install for a different account and cannot verify that grant.
                user = identity.User;
                if (user?.Value != requestedSid) return 6;
                if (TrafficServiceIdentity.IsInstalled()) return 0;
            }

            var assembly = Assembly.GetExecutingAssembly();
            var name = TrafficPreviewPayload.ArchiveName;
            using var archive = assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("Missing embedded traffic archive.");
            using var manifest = assembly.GetManifestResourceStream(name + ".manifest.json") ?? throw new InvalidDataException("Missing embedded traffic manifest.");
            using var verified = TrafficPreviewPayload.OpenVerified(archive, archive.Length, manifest, manifest.Length, out var digest);
            if (user != null)
                TrafficServiceInstall.Install(TrafficServiceStaging.Stage(verified, digest), user);
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or
            KeyNotFoundException or ArgumentException or FormatException or UnauthorizedAccessException or System.Security.SecurityException or Win32Exception)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
