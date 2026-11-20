using System.Text;
using System.Text.Json.Nodes;
using Pane.Services.Screenshots;
using Pane.Settings;

namespace Pane.Services.Clipboard;

internal sealed partial class DataStorage
{
    const string ScreenshotPrefix = "screenshots/";
    const string ScreenshotIndex = ScreenshotPrefix + "index.json";
    const string ScreenshotLayout = nameof(PaneSettings.DesktopScreenshots);
    static readonly string[] EditableAssets = ["image.png", "source.png", "document.json"];

    static bool TransferId(ReadOnlySpan<char> leaf, out Guid id) =>
        Guid.TryParseExact(leaf, "N", out id) && id != Guid.Empty && leaf.SequenceEqual(id.ToString("N"));

    static bool TryScreenshotName(string name, out Guid id)
    {
        id = default;
        if (!name.StartsWith(ScreenshotPrefix, StringComparison.Ordinal)) return false;
        var leaf = name[ScreenshotPrefix.Length..];
        if (leaf.Length == 36 && leaf.EndsWith(".png", StringComparison.Ordinal)) return TransferId(leaf.AsSpan(0, 32), out id);
        return leaf.Length > 37 && leaf.AsSpan(32, 5).SequenceEqual(".pin/") && EditableAssets.Contains(leaf[37..]) &&
            TransferId(leaf.AsSpan(0, 32), out id);
    }

    static DirectoryInfo? TransferDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (File.Exists(path) || directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw Invalid();
        return directory.Exists ? directory : null;
    }

    static Dictionary<string, string> ScreenshotFiles(string data, bool includeRevisions = false)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = TransferDirectory(Path.Combine(data, "screenshots"));
        if (root == null) return result;
        foreach (var entry in root.EnumerateFileSystemInfos())
        {
            var name = ScreenshotPrefix + entry.Name;
            if (TryScreenshotName(name, out _)) { Add(name, entry); continue; }
            if (!entry.Name.EndsWith(".pin", StringComparison.Ordinal) || !TransferId(entry.Name.AsSpan(0, entry.Name.Length - 4), out _)) continue;
            var folder = TransferDirectory(entry.FullName) ?? throw Invalid();
            foreach (var asset in folder.EnumerateFileSystemInfos())
            {
                if (EditableAssets.Contains(asset.Name) || includeRevisions && asset.Name == "current") Add(name + "/" + asset.Name, asset);
                if (!includeRevisions || !TransferId(asset.Name, out _)) continue;
                var revision = TransferDirectory(asset.FullName) ?? throw Invalid();
                foreach (var file in revision.EnumerateFileSystemInfos())
                    if (file.Name is "image.png" or "document.json") Add(name + "/" + asset.Name + "/" + file.Name, file);
            }
        }
        return result;

        void Add(string name, FileSystemInfo file)
        {
            if (file is not FileInfo || (file.Attributes & FileAttributes.ReparsePoint) != 0) throw Invalid();
            result.Add(name, file.FullName);
        }
    }

    static Dictionary<Guid, bool> ScreenshotEntries(IEnumerable<string> names)
    {
        var groups = new Dictionary<Guid, (int Assets, bool Editable)>();
        foreach (var name in names)
        {
            if (!TryScreenshotName(name, out var id)) throw Invalid();
            bool editable = name.Length > ScreenshotPrefix.Length + 36;
            var existing = groups.GetValueOrDefault(id);
            if (existing.Assets > 0 && existing.Editable != editable) throw Invalid();
            groups[id] = (existing.Assets + 1, editable);
        }
        if (groups.Values.Any(group => group.Assets != (group.Editable ? EditableAssets.Length : 1))) throw Invalid();
        return groups.ToDictionary(pair => pair.Key, pair => pair.Value.Editable);
    }

    static int ExportScreenshots(string data, JsonObject? settings, Dictionary<string, byte[]> contents, Dictionary<string, string> files)
    {
        var local = ScreenshotFiles(data);
        var entries = ScreenshotEntries(local.Keys);
        var times = new JsonObject();
        var pins = new JsonObject();
        var positions = settings?[ScreenshotLayout] as JsonObject;
        foreach (var (id, editable) in entries)
        {
            var prefix = ScreenshotPrefix + id.ToString("N");
            var image = local[editable ? prefix + ".pin/image.png" : prefix + ".png"];
            times[id.ToString("D")] = File.GetCreationTimeUtc(image);
            if (positions != null && positions.TryGetPropertyValue(id.ToString("D"), out var position)) pins[id.ToString("D")] = position?.DeepClone();
            if (!editable) { ValidateScreenshotImage(image); files.Add(prefix + ".png", image); continue; }
            var folder = Path.GetDirectoryName(image)!;
            var revision = ScreenshotStore.RevisionDirectory(folder, ScreenshotStore.ReadRevision(folder));
            var original = local[prefix + ".pin/source.png"];
            ValidateScreenshotAssets(revision, original);
            files.Add(prefix + ".pin/source.png", original);
            files.Add(prefix + ".pin/image.png", Path.Combine(revision, "image.png"));
            files.Add(prefix + ".pin/document.json", Path.Combine(revision, "document.json"));
        }
        contents.Add(ScreenshotIndex, Encoding.UTF8.GetBytes(new JsonObject { ["createdUtc"] = times, ["pins"] = pins }.ToJsonString()));
        return entries.Count;
    }

    static (uint Width, uint Height) ValidateScreenshotImage(string path)
    {
        using var image = ScreenshotStore.OpenSessionFile(Path.GetDirectoryName(path)!, Path.GetFileName(path), ScreenshotStore.MaxFileBytes);
        return ScreenshotStore.ReadSize(image);
    }

    static void ValidateScreenshotAssets(string revision, string original)
    {
        var size = ValidateScreenshotImage(original);
        if (ValidateScreenshotImage(Path.Combine(revision, "image.png")) != size) throw Invalid();
        using var document = ScreenshotStore.OpenSessionFile(revision, "document.json", ScreenshotStore.MaxDocumentBytes);
        ScreenshotStore.ValidateDocument(document, size);
    }

    static void ValidateScreenshotPayload(string root, int? expectedCount = null)
    {
        var indexPath = Path.Combine(root, ScreenshotIndex);
        if (!File.Exists(indexPath)) return;
        var files = ScreenshotFiles(root);
        var entries = ScreenshotEntries(files.Keys);
        var index = ReadObject(indexPath);
        var times = index["createdUtc"]!.AsObject();
        if (times.Count != entries.Count || expectedCount is { } count && count != entries.Count) throw Invalid();
        foreach (var (id, editable) in entries)
        {
            var time = times[id.ToString("D")]?.GetValue<DateTime>();
            if (time is not { Kind: DateTimeKind.Utc } utc || utc.Year < 1601) throw Invalid();
            var prefix = ScreenshotPrefix + id.ToString("N");
            if (editable)
            {
                var original = files[prefix + ".pin/source.png"];
                ValidateScreenshotAssets(Path.GetDirectoryName(original)!, original);
            }
            else ValidateScreenshotImage(files[prefix + ".png"]);
        }
        ValidateCardPins(index["pins"]!.AsObject(), entries.ContainsKey, ScreenshotStore.DesktopPinLimit);
    }

    static void ApplyScreenshots(string data, string root, bool merge, JsonObject settings, Dictionary<string, string?> writes)
    {
        var local = ScreenshotFiles(data, includeRevisions: true);
        var incoming = ScreenshotFiles(root);
        var imported = ScreenshotEntries(incoming.Keys).Keys.ToHashSet();
        if (merge)
        {
            foreach (var name in local.Keys)
                if (TryScreenshotName(name, out var id)) imported.Remove(id);
        }
        else
            foreach (var path in local.Values) writes[path] = null;
        var index = ReadObject(Path.Combine(root, ScreenshotIndex));
        var times = index["createdUtc"]!.AsObject();
        foreach (var (name, source) in incoming)
        {
            if (!TryScreenshotName(name, out var id) || !imported.Contains(id)) continue;
            File.SetCreationTimeUtc(source, times[id.ToString("D")]!.GetValue<DateTime>());
            writes[Path.Combine(data, name.Replace('/', Path.DirectorySeparatorChar))] = source;
        }
        var pins = merge ? settings[ScreenshotLayout]?.DeepClone() as JsonObject ?? new JsonObject() : new JsonObject();
        foreach (var pin in index["pins"]!.AsObject())
            if (pins.Count < ScreenshotStore.DesktopPinLimit && !pins.ContainsKey(pin.Key) && imported.Contains(Guid.Parse(pin.Key))) pins[pin.Key] = pin.Value?.DeepClone();
        settings[ScreenshotLayout] = pins.Count == 0 ? null : pins;
    }
}
