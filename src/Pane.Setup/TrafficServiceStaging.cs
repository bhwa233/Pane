using System.IO.Compression;
using System.Security.Cryptography;
using Spark.Traffic;

internal static class TrafficServiceStaging
{
    internal static string Stage(ZipArchive payload, string digest)
    {
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid verified archive digest.");
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!Path.IsPathFullyQualified(programData)) throw new IOException("Cannot locate machine application data.");
        using var programDataPin = DirectoryGuard.Pin(programData);
        var pane = Path.Combine(programData, "Pane");
        DirectoryGuard.CreateProtected(pane, true);
        using var panePin = DirectoryGuard.OpenProtected(pane, true);
        var traffic = Path.Combine(pane, "traffic");
        DirectoryGuard.CreateProtected(traffic, true);
        using var trafficPin = DirectoryGuard.OpenProtected(traffic, true);
        var versions = Path.Combine(traffic, "service");
        DirectoryGuard.CreateProtected(versions, true);
        using var versionPin = DirectoryGuard.OpenProtected(versions, true);

        var version = Path.Combine(versions, "sha256-" + digest);
        if (Directory.Exists(version))
        {
            VerifyTree(payload, version);
            return version;
        }

        var staging = Path.Combine(versions, "staging-" + Guid.NewGuid().ToString("N"));
        var files = new List<string>();
        var directories = new List<string>();
        try
        {
            CreateDirectories(versions, staging, directories);
            using (DirectoryGuard.OpenProtected(staging, true))
            {
                foreach (var entry in payload.Entries)
                {
                    var target = Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    CreateDirectories(staging, Path.GetDirectoryName(target)!, directories);
                    using var output = DirectoryGuard.CreateCodeFile(target);
                    files.Add(target);
                    using var input = entry.Open();
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                VerifyTree(payload, staging);
            }

            try { Directory.Move(staging, version); }
            catch (IOException) when (Directory.Exists(version)) { VerifyTree(payload, version); }
            return version;
        }
        finally
        {
            foreach (var file in files) TryRemove(() => File.Delete(file));
            for (var index = directories.Count - 1; index >= 0; index--)
            {
                var directory = directories[index];
                TryRemove(() => Directory.Delete(directory));
            }
        }
    }

    static void CreateDirectories(string root, string path, List<string> created)
    {
        if (path == root) return;
        CreateDirectories(root, Path.GetDirectoryName(path)!, created);
        if (Directory.Exists(path)) return;
        DirectoryGuard.CreateProtected(path, true);
        created.Add(path);
    }

    static void VerifyTree(ZipArchive payload, string root)
    {
        using var pinned = DirectoryGuard.OpenProtected(root, true);
        var assets = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in payload.Entries)
        {
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            assets.Add(relative, entry);
            for (var parent = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                directories.Add(parent);
        }
        VerifyDirectory(root, "", assets, directories);
        if (assets.Count != 0 || directories.Count != 0) throw new InvalidDataException("Incomplete traffic service directory.");
    }

    static void VerifyDirectory(string root, string relative, Dictionary<string, ZipArchiveEntry> assets, HashSet<string> directories)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(root, relative)))
        {
            var name = Path.Combine(relative, Path.GetFileName(path));
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected traffic service reparse point.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!directories.Remove(name)) throw new InvalidDataException("Unexpected traffic service directory.");
                using var pinned = DirectoryGuard.OpenProtected(path, true);
                VerifyDirectory(root, name, assets, directories);
                continue;
            }
            if (!assets.Remove(name, out var expected)) throw new InvalidDataException("Unexpected traffic service file.");
            using var actual = DirectoryGuard.OpenInput(path);
            DirectoryGuard.CheckCodeFile(path);
            using var source = expected.Open();
            if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual), SHA256.HashData(source)))
                throw new InvalidDataException("Traffic service file digest mismatch after staging.");
        }
    }

    static void TryRemove(Action delete)
    {
        try { delete(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
