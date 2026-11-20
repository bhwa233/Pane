using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class TrafficPreviewPayload
{
    const string DirectoryName = "TrafficService";
    internal const string ArchiveName = "Pane.TrafficService-x64-preview.zip";
    const int MaxArchiveBytes = 32 * 1024 * 1024;
    const int MaxExpandedBytes = 96 * 1024 * 1024;
    const int MaxManifestBytes = 64 * 1024;
    const int MaxFiles = 256;
    static readonly string[] requiredFiles = ["Pane.TrafficService.exe", "Pane.TrafficService.exe.config",
        "Pane.Traffic.Core.dll", "Microsoft.Diagnostics.Tracing.TraceEvent.dll", "e_sqlite3.dll"];

    readonly record struct Asset(string Path, long Bytes, string Sha256);

    public static void Verify(ZipArchive payload)
    {
        ZipArchiveEntry? archive = null, manifest = null;
        foreach (var entry in payload.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (!path.Equals(DirectoryName, StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith(DirectoryName + "/", StringComparison.OrdinalIgnoreCase)) continue;
            switch (path)
            {
                case DirectoryName + "/" + ArchiveName when archive == null:
                    archive = entry;
                    break;
                case DirectoryName + "/" + ArchiveName + ".manifest.json" when manifest == null:
                    manifest = entry;
                    break;
                default:
                    throw new InvalidDataException("Unexpected traffic preview payload entry.");
            }
        }
        if (archive == null && manifest == null) return;
        if (archive == null || manifest == null) throw new InvalidDataException("Missing traffic preview payload.");

        using var archiveStream = archive.Open();
        using var manifestStream = manifest.Open();
        using var verified = OpenVerified(archiveStream, archive.Length, manifestStream, manifest.Length, out _);
    }

    internal static ZipArchive OpenVerified(Stream archiveStream, long archiveLength, Stream manifestStream, long manifestLength, out string digest)
    {
        if (archiveLength is <= 0 or > MaxArchiveBytes || manifestLength is <= 0 or > MaxManifestBytes)
            throw new InvalidDataException("Missing or oversized traffic preview payload.");

        var bytes = ReadBounded(manifestStream, (int)manifestLength);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("archive").GetString() != ArchiveName)
            throw new InvalidDataException("Invalid traffic preview manifest.");

        var archiveBytes = ReadBounded(archiveStream, (int)archiveLength);
        digest = Convert.ToHexStringLower(SHA256.HashData(archiveBytes));
        if (!digest.Equals(root.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Traffic preview archive digest mismatch.");

        var archiveBuffer = new MemoryStream(archiveBytes, writable: false);
        ZipArchive? inner = null;
        try
        {
            inner = new ZipArchive(archiveBuffer, ZipArchiveMode.Read);
            VerifyFiles(inner, root.GetProperty("files"));
            return inner;
        }
        catch
        {
            if (inner != null) inner.Dispose();
            else archiveBuffer.Dispose();
            throw;
        }
    }

    static byte[] ReadBounded(Stream stream, int length)
    {
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Invalid traffic payload stream length.");
        return bytes;
    }

    static void VerifyFiles(ZipArchive archive, JsonElement files)
    {
        if (files.GetArrayLength() is < 1 or > MaxFiles || archive.Entries.Count != files.GetArrayLength())
            throw new InvalidDataException("Unexpected traffic preview file count.");

        var inventory = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var item in files.EnumerateArray())
        {
            var path = item.GetProperty("path").GetString();
            var size = item.GetProperty("bytes").GetInt64();
            var hash = item.GetProperty("sha256").GetString();
            if (path == null || !ValidPath(path) || size is < 0 or > MaxExpandedBytes ||
                hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit) || !inventory.TryAdd(path, new(path, size, hash)))
                throw new InvalidDataException("Invalid or duplicate traffic preview file record.");
            expanded += size;
            if (expanded > MaxExpandedBytes) throw new InvalidDataException("Traffic preview files are too large.");
        }
        if (requiredFiles.Any(path => !inventory.TryGetValue(path, out var asset) || asset.Path != path || asset.Bytes == 0))
            throw new InvalidDataException("Traffic preview is missing a required runtime file.");
        foreach (var path in inventory.Keys)
            for (var split = path.LastIndexOf('/'); split >= 0; split = path.LastIndexOf('/', split - 1))
                if (inventory.ContainsKey(path[..split])) throw new InvalidDataException("Traffic preview file and directory names conflict.");

        var buffer = new byte[81920];
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in archive.Entries)
        {
            if (!inventory.Remove(entry.FullName, out var asset) || asset.Path != entry.FullName || entry.Length != asset.Bytes)
                throw new InvalidDataException("Traffic preview file does not match the inventory.");

            using var stream = entry.Open();
            for (var left = asset.Bytes; left != 0;)
            {
                var chunk = buffer.AsSpan(0, (int)Math.Min(left, buffer.Length));
                stream.ReadExactly(chunk);
                sha256.AppendData(chunk);
                left -= chunk.Length;
            }
            if (stream.ReadByte() != -1 || !Convert.ToHexString(sha256.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Traffic preview file digest mismatch.");
        }
    }

    static bool ValidPath(string path)
    {
        if (path.Length is < 1 or > 240) return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment.EndsWith('.') || !char.IsAsciiLetterOrDigit(segment[0]) ||
                segment.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))) return false;
            var stem = segment.Split('.', 2)[0].ToUpperInvariant();
            if (stem is "AUX" or "CON" or "NUL" or "PRN" ||
                (stem.Length == 4 && char.IsAsciiDigit(stem[3]) && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)))) return false;
        }
        return true;
    }
}
