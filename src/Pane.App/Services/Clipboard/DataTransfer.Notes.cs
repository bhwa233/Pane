using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Pane.Services.Notes;
using Pane.Settings;

namespace Pane.Services.Clipboard;

internal sealed partial class DataStorage
{
    const string NoteIndex = "notes/index.json";
    const string NoteLayout = nameof(PaneSettings.DesktopNotes);
    const string NoteImagePrefix = "notes/.images/";

    static bool TryNoteImageName(string name, out Guid id)
    {
        id = default;
        if (!name.StartsWith(NoteImagePrefix, StringComparison.Ordinal) || !name.EndsWith(".png", StringComparison.Ordinal)) return false;
        var leaf = name.AsSpan(NoteImagePrefix.Length, name.Length - NoteImagePrefix.Length - 4);
        return leaf.Length == 65 && leaf[32] == '-' && Guid.TryParseExact(leaf[..32], "N", out id) && id != Guid.Empty &&
            Guid.TryParseExact(leaf[33..], "N", out var image) && image != Guid.Empty && leaf.SequenceEqual($"{id:N}-{image:N}");
    }

    static bool TryNoteName(string name, out Guid id, out long? ticks)
    {
        id = default;
        ticks = null;
        if (!name.StartsWith("notes/", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) return false;
        var leaf = name.AsSpan(6, name.Length - 11);
        if (leaf.StartsWith(".history/", StringComparison.Ordinal))
        {
            leaf = leaf[9..];
            if (leaf.Length != 52 || leaf[32] != '-' ||
                !long.TryParse(leaf[33..], NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
                version > DateTimeOffset.MaxValue.UtcTicks) return false;
            ticks = version;
            leaf = leaf[..32];
        }
        return leaf.Length == 32 && Guid.TryParseExact(leaf, "N", out id) && id != Guid.Empty && leaf.SequenceEqual(id.ToString("N"));
    }

    static Dictionary<string, string> NoteFiles(string data)
    {
        const FileAttributes unsafeAttributes = FileAttributes.ReparsePoint;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in new[] { "notes", "notes/.history", "notes/.images" })
        {
            var directory = new DirectoryInfo(Path.Combine(data, relative));
            if (!directory.Exists) continue;
            if ((directory.Attributes & unsafeAttributes) != 0) throw Invalid();
            var images = relative == "notes/.images";
            foreach (var file in directory.EnumerateFiles(images ? "*.png" : "*.json"))
            {
                var name = relative + "/" + file.Name;
                if (images ? !TryNoteImageName(name, out _) : !TryNoteName(name, out _, out _)) continue;
                if ((file.Attributes & unsafeAttributes) != 0) throw Invalid();
                if (file.Length > (images ? NoteStore.ImageByteLimit : ImportJsonLimit)) throw Invalid();
                result.Add(name, file.FullName);
            }
        }
        return result;
    }

    static int ExportNotes(string data, JsonObject? settings, Dictionary<string, byte[]> contents, Dictionary<string, string> files)
    {
        var names = NoteFiles(data);
        var pins = new JsonObject();
        var count = 0;
        var versions = 0;
        var images = new HashSet<string>(StringComparer.Ordinal);
        if (names.Count > 0)
        {
            var store = new NoteStore(Path.Combine(data, "notes"));
            store.Load();
            if (store.UnreadableCount != 0) throw Invalid();
            var positions = settings?[NoteLayout] as JsonObject;
            foreach (var note in store.DocumentsInStorage)
            {
                var positionKey = note.Id.ToString("D");
                if (!note.Recycled && positions != null && positions.TryGetPropertyValue(positionKey, out var position)) pins[positionKey] = position?.DeepClone();
                Add(store.PathFor(note.Id));
                CollectImages(data, store, note, images);
                foreach (var version in store.Versions(note.Id))
                {
                    CollectImages(data, store, store.ReadVersion(note.Id, version), images);
                    Add(store.VersionPath(note.Id, version));
                    versions++;
                }
                count++;
            }
        }
        foreach (var image in images) Add(Path.Combine(data, image));
        contents.Add(NoteIndex, Encoding.UTF8.GetBytes(new JsonObject { ["count"] = count, ["versions"] = versions, ["images"] = images.Count, ["pins"] = pins }.ToJsonString()));
        return count;

        void Add(string path)
        {
            var name = Path.GetRelativePath(data, path).Replace(Path.DirectorySeparatorChar, '/');
            if (!names.TryGetValue(name, out var safePath)) throw Invalid();
            files.Add(name, safePath);
        }
    }

    static void CollectImages(string root, NoteStore store, NoteDocument note, HashSet<string> images)
    {
        foreach (var image in note.Images ?? [])
        {
            var name = Path.GetRelativePath(root, store.ImagePath(note.Id, image)).Replace(Path.DirectorySeparatorChar, '/');
            if (images.Add(name) && store.FindImage(note.Id, image) == null) throw Invalid();
        }
    }

    static void ValidateNotePayload(string root, int? expectedCount = null)
    {
        var indexPath = Path.Combine(root, NoteIndex);
        if (!File.Exists(indexPath)) return;
        var files = NoteFiles(root);
        var index = ReadObject(indexPath);
        var store = new NoteStore(Path.Combine(root, "notes"));
        store.Load();
        var count = store.DocumentsInStorage.Count();
        var imageNames = files.Keys.Where(name => TryNoteImageName(name, out _)).ToHashSet(StringComparer.Ordinal);
        if (store.UnreadableCount != 0 || count != index["count"]!.GetValue<int>() || files.Count - count - imageNames.Count != index["versions"]!.GetValue<int>() ||
            imageNames.Count != (index["images"]?.GetValue<int>() ?? 0) ||
            expectedCount is { } expected && count != expected) throw Invalid();
        ValidateCardPins(index["pins"]!.AsObject(), id => store.Find(id) != null, NoteStore.DesktopPinLimit);
        var versions = new Dictionary<Guid, HashSet<long>>();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in store.DocumentsInStorage) CollectImages(root, store, note, referenced);
        foreach (var name in files.Keys)
        {
            TryNoteName(name, out var id, out var ticks);
            if (ticks is not { } version) continue;
            if (!versions.TryGetValue(id, out var allowed))
                versions.Add(id, allowed = store.Versions(id).Select(date => date.UtcTicks).ToHashSet());
            if (!allowed.Contains(version)) throw Invalid();
            CollectImages(root, store, store.ReadVersion(id, new DateTimeOffset(version, TimeSpan.Zero)), referenced);
        }
        if (!imageNames.SetEquals(referenced)) throw Invalid();
    }

    static void ValidateCardPins(JsonObject pins, Func<Guid, bool> contains, int limit)
    {
        if (pins.Count > limit) throw Invalid();
        foreach (var pin in pins)
        {
            if (!Guid.TryParseExact(pin.Key, "D", out var id) || pin.Key != id.ToString("D") || !contains(id)) throw Invalid();
            if (pin.Value is { } value && (string.IsNullOrWhiteSpace(value["MonitorId"]?.GetValue<string>()) ||
                !double.IsFinite(value["X"]?.GetValue<double>() ?? double.NaN) || !double.IsFinite(value["Y"]?.GetValue<double>() ?? double.NaN))) throw Invalid();
        }
    }

    static void ApplyNotes(string data, string root, bool merge, JsonObject settings, Dictionary<string, string?> writes)
    {
        var local = NoteFiles(data);
        var incoming = NoteFiles(root);
        var skipped = new HashSet<Guid>();
        if (merge)
            foreach (var name in local.Keys)
                if (TryNoteName(name, out var id, out var ticks) && ticks == null) skipped.Add(id);
        var imported = new HashSet<Guid>();
        foreach (var name in incoming.Keys)
            if (TryNoteName(name, out var id, out var ticks) && ticks == null && !skipped.Contains(id)) imported.Add(id);
        foreach (var pair in local)
            if (!merge || OwnerImported(pair.Key)) writes[pair.Value] = null;
        foreach (var pair in incoming)
            if (OwnerImported(pair.Key)) writes[Path.Combine(data, pair.Key.Replace('/', Path.DirectorySeparatorChar))] = pair.Value;
        var pins = merge ? settings[NoteLayout]?.DeepClone() as JsonObject ?? new JsonObject() : new JsonObject();
        foreach (var pin in ReadObject(Path.Combine(root, NoteIndex))["pins"]!.AsObject())
            if (pins.Count < NoteStore.DesktopPinLimit && !pins.ContainsKey(pin.Key) && imported.Contains(Guid.Parse(pin.Key))) pins[pin.Key] = pin.Value?.DeepClone();
        settings[NoteLayout] = pins.Count == 0 ? null : pins;

        bool OwnerImported(string name) => (TryNoteName(name, out var id, out _) || TryNoteImageName(name, out id)) && imported.Contains(id);
    }
}
