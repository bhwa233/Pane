using System.IO.Compression;
using System.Security.Cryptography;

namespace Pane.Services.Ai;

internal static partial class OfflineDictionary
{
    const string DataFolder = "DictionaryData", DatabaseName = "ecdict.sqlite";
    static readonly Dictionary<string, (int Bytes, string Digest)> PackageFiles = new(StringComparer.Ordinal)
    {
        [DatabaseName] = (102096896, "D7C046F2BA43D2630CF276FB5B790F85C8F3D3F26CA12C1B7EF29D5DB995A489"),
        ["ECDICT-LICENSE.txt"] = (1063, "F8552DD246F61A4E064569EAE6194A01C6B3D63B03BF27C6CA863593C549ED0F"),
        ["provenance.json"] = (1038, "8ADD62E09D4657DAA94B2A169B538271132B0D1BCE00ACA48D4911BF0EA94ABD")
    };

    internal static void InstallPackage(string path, string dataDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var input = File.OpenRead(path);
        if (input.Length > 64 * 1024 * 1024) throw new InvalidDataException("Dictionary package is too large.");
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count != PackageFiles.Count) throw new InvalidDataException("Unexpected dictionary package inventory.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<(ZipArchiveEntry Entry, bool Database, string Digest)>(PackageFiles.Count);
        var prefix = DataFolder + "/";
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected dictionary package path.");
            var name = entry.FullName[prefix.Length..];
            if (!PackageFiles.TryGetValue(name, out var expected) || !seen.Add(name) || entry.Length != expected.Bytes)
                throw new InvalidDataException("Unexpected dictionary package entry.");
            entries.Add((entry, name == DatabaseName, expected.Digest));
        }

        var directory = Path.Combine(dataDirectory, DataFolder);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".ecdict-{Guid.NewGuid():N}.tmp");
        var buffer = new byte[64 * 1024];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            foreach (var (entry, database, digest) in entries)
            {
                using var source = entry.Open();
                using var target = database ? new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None) : null;
                var remaining = entry.Length;
                int count;
                while ((count = source.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    remaining -= count;
                    if (remaining < 0) throw new InvalidDataException("Dictionary entry exceeded its declared size.");
                    hash.AppendData(buffer, 0, count);
                    target?.Write(buffer, 0, count);
                }
                if (remaining != 0 || Convert.ToHexString(hash.GetHashAndReset()) != digest)
                    throw new InvalidDataException("Dictionary package checksum mismatch.");
                target?.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(directory, DatabaseName), overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
