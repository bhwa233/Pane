using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

internal static class TrafficPreviewPayloadTests
{
    const string ArchiveName = "Pane.TrafficService-x64-preview.zip";
    const string ArchivePath = "TrafficService/" + ArchiveName;
    static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static int Run(string? actualPayload = null)
    {
        (string Path, byte[] Bytes)[] files = [.. new[] { "Pane.TrafficService.exe", "Pane.TrafficService.exe.config", "Pane.Traffic.Core.dll",
            "Microsoft.Diagnostics.Tracing.TraceEvent.dll", "e_sqlite3.dll", "Dependencies/example/1.0/LICENSE.TXT" }
            .Select(path => (path, Encoding.UTF8.GetBytes(path)))];
        using var standard = Open(Zip(("Pane.App.exe", "pane"u8.ToArray())));
        TrafficPreviewPayload.Verify(standard);
        Accept(files);
        Reject("altered file with valid outer digest", files, FileList(files).Replace(Digest(files[0].Bytes), new string('0', 64)));
        Reject("case-insensitive alias", [.. files, ("dependencies/example/1.0/license.txt", "alias"u8.ToArray())]);
        Reject("escaping path", [.. files, ("Dependencies/../extra.dll", "unexpected"u8.ToArray())]);
        Reject("file used as parent", [.. files, ("Dependencies/example", "file"u8.ToArray())]);
        Reject("unlisted ZIP entry", [.. files, ("extra.dll", "unexpected"u8.ToArray())], FileList(files));
        Reject("missing required runtime", files[1..]);
        if (actualPayload == null) return 8;
        using var real = ZipFile.OpenRead(actualPayload);
        if (real.GetEntry(ArchivePath) == null) throw new InvalidDataException("Real test payload is missing its traffic archive.");
        TrafficPreviewPayload.Verify(real);
        return 9;
    }

    static void Accept((string Path, byte[] Bytes)[] files, string? inventory = null)
    {
        var inner = Zip(files);
        var manifest = Encoding.UTF8.GetBytes($$"""{"schema":1,"archive":"{{ArchiveName}}","sha256":"{{Digest(inner)}}","files":[{{inventory ?? FileList(files)}}]}""");
        using var outer = Open(Zip((ArchivePath, inner), (ArchivePath + ".manifest.json", manifest)));
        TrafficPreviewPayload.Verify(outer);
    }

    static void Reject(string reason, (string Path, byte[] Bytes)[] files, string? inventory = null)
    {
        try { Accept(files, inventory); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Traffic preview accepted: " + reason);
    }

    static string FileList((string Path, byte[] Bytes)[] files) => string.Join(',', files.Select(file =>
        $$"""{"path":"{{file.Path}}","bytes":{{file.Bytes.Length}},"sha256":"{{Digest(file.Bytes)}}"}"""));

    static ZipArchive Open(byte[] bytes) => new(new MemoryStream(bytes), ZipArchiveMode.Read);

    static byte[] Zip(params (string Path, byte[] Bytes)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                using var output = archive.CreateEntry(file.Path).Open();
                output.Write(file.Bytes);
            }
        return buffer.ToArray();
    }
}
