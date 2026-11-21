using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Pane.Services.Notes;
using Pane.Settings;

namespace Pane.Services.Clipboard;

/// <summary>便签备份（notes-mvp §4）：对象收敛到 MVP 文本记录及历史；.images 目录原样保留，
/// 不被读取、不进入备份。pins 只含 DesktopNotes 布局；数据路径、热键和显示器绑定是本机设置。</summary>
internal sealed partial class DataStorage
{
    const string NoteIndex = "notes/index.json";
    const string NoteLayout = nameof(PaneSettings.DesktopNotes);

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
        foreach (var relative in new[] { "notes", "notes/.history" })
        {
            var directory = new DirectoryInfo(Path.Combine(data, relative));
            if (!directory.Exists) continue;
            if ((directory.Attributes & unsafeAttributes) != 0) throw Invalid();
            foreach (var file in directory.EnumerateFiles("*.json"))
            {
                var name = relative + "/" + file.Name;
                if (!TryNoteName(name, out _, out _)) continue;
                if ((file.Attributes & unsafeAttributes) != 0) throw Invalid();
                if (file.Length > ImportJsonLimit) throw Invalid();
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
                foreach (var version in store.Versions(note.Id))
                {
                    Add(store.VersionPath(note.Id, version));
                    versions++;
                }
                count++;
            }
        }
        contents.Add(NoteIndex, Encoding.UTF8.GetBytes(new JsonObject { ["count"] = count, ["versions"] = versions, ["pins"] = pins }.ToJsonString()));
        return count;

        void Add(string path)
        {
            var name = Path.GetRelativePath(data, path).Replace(Path.DirectorySeparatorChar, '/');
            if (!names.TryGetValue(name, out var safePath)) throw Invalid();
            files.Add(name, safePath);
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
        if (store.UnreadableCount != 0 || count != index["count"]!.GetValue<int>() || files.Count - count != index["versions"]!.GetValue<int>() ||
            expectedCount is { } expected && count != expected) throw Invalid();
        ValidateCardPins(index["pins"]!.AsObject(), id => store.Find(id) != null, NoteStore.DesktopPinLimit);
        var versions = new Dictionary<Guid, HashSet<long>>();
        foreach (var name in files.Keys)
        {
            TryNoteName(name, out var id, out var ticks);
            if (ticks is not { } version) continue;
            if (!versions.TryGetValue(id, out var allowed))
                versions.Add(id, allowed = store.Versions(id).Select(date => date.UtcTicks).ToHashSet());
            if (!allowed.Contains(version)) throw Invalid();
        }
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

        bool OwnerImported(string name) => TryNoteName(name, out var id, out _) && imported.Contains(id);
    }
}
