using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Pane.Services.Screenshots;

namespace Pane.Services.Clipboard;

internal sealed partial class DataStorage
{
    const string OcrPrefix = OcrHistoryStore.DirectoryName + "/";
    const string OcrIndex = OcrPrefix + "index.json";
    const int OcrIndexBytes = 8192;
    static readonly string[] OcrAssets = [OcrHistoryStore.ImageName, OcrHistoryStore.ResultName, OcrHistoryStore.CorrectionsName];

    static int OcrAssetLimit(string name) => name switch
    {
        OcrHistoryStore.ImageName => ScreenshotStore.MaxFileBytes,
        OcrHistoryStore.ResultName => NativeScreenshotOcr.MaxResultBytes,
        OcrHistoryStore.CorrectionsName => OcrHistoryStore.MaxCorrectionBytes,
        _ => 0
    };

    static bool TryOcrName(string name, out Guid id, out int limit)
    {
        id = default;
        limit = 0;
        if (!name.StartsWith(OcrPrefix, StringComparison.Ordinal)) return false;
        var leaf = name.AsSpan(OcrPrefix.Length);
        if (leaf.Length <= 33 || leaf[32] != '/' || !TransferId(leaf[..32], out id)) return false;
        limit = OcrAssetLimit(leaf[33..].ToString());
        return limit != 0;
    }

    static void ValidateOcrArchive(ZipArchive zip, JsonObject manifest, int format)
    {
        var entries = zip.Entries.Where(entry => entry.FullName.StartsWith(OcrPrefix, StringComparison.OrdinalIgnoreCase));
        if (format != 3)
        {
            if (manifest.ContainsKey("ocrHistory") || entries.Any()) throw Invalid();
            return;
        }
        if (manifest["ocrHistory"]?.GetValue<int>() is not (>= 0 and <= OcrHistoryStore.RecordLimit) ||
            zip.GetEntry(OcrIndex) is not { Length: > 0 and <= OcrIndexBytes }) throw Invalid();
        var records = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            if (entry.FullName == OcrIndex) continue;
            if (!TryOcrName(entry.FullName, out var id, out int limit) || entry.Length == 0 || entry.Length > limit) throw Invalid();
            if (records.Add(id) && records.Count > OcrHistoryStore.RecordLimit) throw Invalid();
        }
        if (records.Count != manifest["ocrHistory"]!.GetValue<int>()) throw Invalid();
        foreach (var id in records)
        {
            var path = OcrPrefix + id.ToString("N") + "/";
            if (zip.GetEntry(path + OcrHistoryStore.ImageName) == null || zip.GetEntry(path + OcrHistoryStore.ResultName) == null) throw Invalid();
        }
    }

    static int ExportOcrHistory(string data, Dictionary<string, byte[]> contents, Dictionary<string, string> files)
    {
        var directory = Path.Combine(data, OcrHistoryStore.DirectoryName);
        _ = TransferDirectory(directory);
        var entries = new OcrHistoryStore(directory).List();
        if (entries.Length > OcrHistoryStore.RecordLimit) throw Invalid();
        var index = new JsonObject();
        foreach (var entry in entries)
        {
            var key = entry.Id.ToString("N");
            var folder = TransferDirectory(Path.Combine(directory, key)) ?? throw Invalid();
            OcrHistoryStore.ValidateRecord(folder.FullName, entry.Id);
            index.Add(key, entry.CreatedUtc);
            foreach (var asset in OcrAssets)
                if (File.Exists(Path.Combine(folder.FullName, asset))) files.Add(OcrPrefix + key + "/" + asset, Path.Combine(folder.FullName, asset));
        }
        contents.Add(OcrIndex, Encoding.UTF8.GetBytes(index.ToJsonString()));
        return entries.Length;
    }

    static Dictionary<Guid, DateTime> ReadOcrIndex(string root)
    {
        var path = Path.Combine(root, OcrIndex);
        using var stream = ScreenshotStore.OpenSessionFile(Path.GetDirectoryName(path)!, Path.GetFileName(path), OcrIndexBytes);
        if (JsonNode.Parse(stream) is not JsonObject index || index.Count > OcrHistoryStore.RecordLimit) throw Invalid();
        var records = new Dictionary<Guid, DateTime>();
        foreach (var pair in index)
        {
            if (!TransferId(pair.Key, out var id) || pair.Value?.GetValue<DateTime>() is not { Kind: DateTimeKind.Utc } created || created.Year < 1601) throw Invalid();
            records.Add(id, created);
        }
        return records;
    }

    static Dictionary<Guid, DateTime> ValidateOcrPayload(string root, int? expected = null)
    {
        var directory = TransferDirectory(Path.Combine(root, OcrHistoryStore.DirectoryName)) ?? throw Invalid();
        var records = ReadOcrIndex(root);
        var found = new HashSet<Guid>();
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            if (item.Name == Path.GetFileName(OcrIndex)) continue;
            if (item is not DirectoryInfo folder || (folder.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !TransferId(folder.Name, out var id) || !records.ContainsKey(id) || !found.Add(id)) throw Invalid();
            OcrHistoryStore.ValidateRecord(folder.FullName, id);
        }
        if (found.Count != records.Count || expected is int count && count != records.Count) throw Invalid();
        return records;
    }

    static void ApplyOcrHistory(string data, string root, bool merge, Dictionary<Guid, DateTime> incoming, Dictionary<string, string?> writes)
    {
        var localRoot = Path.Combine(data, OcrHistoryStore.DirectoryName);
        var stagedRoot = Path.Combine(root, OcrHistoryStore.DirectoryName);
        _ = TransferDirectory(localRoot);
        var local = new OcrHistoryStore(localRoot).List().Select(entry => entry.Id).ToHashSet();
        if (merge && local.Union(incoming.Keys).Count() > OcrHistoryStore.RecordLimit) throw Invalid();
        var changed = merge ? incoming.Keys.Except(local) : incoming.Keys.Union(local);
        foreach (var id in changed)
        {
            var target = Path.Combine(localRoot, id.ToString("N"));
            var directory = TransferDirectory(target);
            if (directory != null && directory.EnumerateFileSystemInfos().Any(item => OcrAssets.Any(asset =>
                string.Equals(item.Name, asset + ".import-tmp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, asset, StringComparison.OrdinalIgnoreCase) &&
                (item is not FileInfo || (item.Attributes & FileAttributes.ReparsePoint) != 0)))) throw Invalid();
            foreach (var asset in OcrAssets)
            {
                var source = Path.Combine(stagedRoot, id.ToString("N"), asset);
                var destination = Path.Combine(target, asset);
                if (File.Exists(source))
                {
                    if (asset == OcrHistoryStore.ResultName) File.SetCreationTimeUtc(source, incoming[id]);
                    writes.Add(destination, source);
                }
                else if (File.Exists(destination)) writes.Add(destination, null);
            }
        }
    }
}
