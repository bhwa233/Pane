using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var hash = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "PanePayloadSha256").Value;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pane", "bundles");
            var directory = Path.Combine(root, hash!);
            if (!Directory.Exists(directory))
            {
                var staging = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    using var payload = assembly.GetManifestResourceStream("payload.zip") ?? throw new InvalidDataException("Pane 程序包缺失");
                    using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
                    archive.ExtractToDirectory(staging);
                    try { Directory.Move(staging, directory); }
                    // Two launches can extract the same bundle, but only a complete directory can win the rename.
                    catch (IOException) when (Directory.Exists(directory)) { }
                }
                finally
                {
                    if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                }
            }

            var start = new ProcessStartInfo(Path.Combine(directory, "Pane.App.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.Environment["PANE_PORTABLE_EXE"] = Environment.ProcessPath!;
            foreach (var arg in args) start.ArgumentList.Add(arg == "--portable-autostart" ? "--autostart" : arg);
            using var process = Process.Start(start) ?? throw new IOException("无法启动 Pane");
            if (args is ["--exit"])
            {
                process.WaitForExit();
                return process.ExitCode;
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBoxW(0, $"无法启动 Pane。\n{error.Message}", "Pane", 0x10);
            return 1;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int MessageBoxW(nint window, string text, string caption, uint flags);
}
