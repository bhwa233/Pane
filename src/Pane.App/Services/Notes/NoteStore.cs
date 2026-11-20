using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pane.Services.Clipboard;
using Pane.Services.Screenshots;

namespace Pane.Services.Notes;

internal sealed record NoteDocument(Guid Id, string Title, string Body, DateTimeOffset Updated)
{
    string category = "";
    public string Category { get => category; init => category = value?.Trim() ?? ""; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid[]? Images { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Recycled { get; init; }
    /// <summary>编辑模式（040，galaxy 对齐）：0 = 纯文本，1 = Markdown。默认 0（旧文件缺省即纯文本）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int EditorMode { get; init; }
}

[JsonSerializable(typeof(NoteDocument))]
internal partial class NoteJson : JsonSerializerContext { }

internal sealed class NoteStore(string directory, object? storageSync = null)
{
    readonly object storageGate = storageSync ?? new object();
    const int VersionLimit = 30;
    internal const int DesktopPinLimit = 8;
    internal const int TitleLimit = 180;
    internal const int BodyLimit = 200_000;
    internal const int CategoryLimit = 64;
    internal const int ImageLimit = 4;
    internal const int ImageByteLimit = 8 * 1024 * 1024;
    internal const uint ImagePixelLimit = 8_000_000;
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
    string HistoryDirectory => Path.Combine(directory, ".history");
    string ImagesDirectory => Path.Combine(directory, ".images");
    Dictionary<Guid, NoteDocument>? documents;
    public int UnreadableCount { get; private set; }
    public event EventHandler? Changed;
    public NoteDocument? Draft { get; private set; }
    public bool DraftIsSaved => Draft is { } draft && documents!.ContainsKey(draft.Id);
    public bool HasChanges => Draft is { } draft &&
        (documents!.TryGetValue(draft.Id, out var saved)
            ? !SameContent(draft, saved)
            : draft.Title.Length != 0 || draft.Body.Length != 0 || draft.Category.Length != 0 || draft.Images is { Length: > 0 } || draft.EditorMode != 0);

    static bool SameContent(NoteDocument first, NoteDocument second) => first.Title == second.Title && first.Body == second.Body &&
        first.Category == second.Category && first.EditorMode == second.EditorMode &&
        first.Images.AsSpan().SequenceEqual(second.Images);

    static bool ValidImages(NoteDocument note) => note.Images is not { } images ||
        images.Length <= ImageLimit && !images.Contains(Guid.Empty) && images.Distinct().Count() == images.Length;

    internal IEnumerable<NoteDocument> DocumentsInStorage => documents!.Values.OrderByDescending(note => note.Updated);
    public IEnumerable<NoteDocument> Documents => DocumentsInStorage.Where(note => !note.Recycled);
    public IEnumerable<NoteDocument> RecycledDocuments => DocumentsInStorage.Where(note => note.Recycled);
    public NoteDocument? Find(Guid id) => documents?.GetValueOrDefault(id) is { Recycled: false } note ? note : null;

    public void Load()
    {
        if (documents != null) return;
        Directory.CreateDirectory(directory);
        var loaded = new Dictionary<Guid, NoteDocument>();
        var unreadable = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) || id == Guid.Empty)
                    continue;
                using var stream = File.OpenRead(file);
                var note = JsonSerializer.Deserialize(stream, NoteJson.Default.NoteDocument);
                if (note == null || note.Id != id || note.Title == null || note.Body == null || !ValidImages(note))
                    unreadable++;
                else
                    loaded.Add(id, note);
            }
            catch (Exception error) when (IsStorageError(error)) { unreadable++; }
        }
        documents = loaded;
        UnreadableCount = unreadable;
    }

    public void Select(Guid? id)
    {
        if (HasChanges) throw new InvalidOperationException("Save the note before changing selection.");
        Draft = id is { } key ? Find(key) ?? throw new KeyNotFoundException() : new(Guid.NewGuid(), "", "", default);
    }

    public void Edit(string title, string body, string? category = null)
    {
        if (Draft is not { } draft) return;
        var normalized = NormalizeNewlines(body);
        if (documents!.TryGetValue(draft.Id, out var saved) && normalized == NormalizeNewlines(saved.Body))
            normalized = saved.Body;
        Draft = draft with { Title = title, Body = normalized, Category = category ?? draft.Category };
    }

    /// <summary>切换当前便签的编辑模式（0 纯文本 / 1 Markdown，galaxy note.setEditorMode 对齐）。已保存条目立即持久化；
    /// 新草稿先记在内存，首次保存时随正文落盘。不产生历史版本。</summary>
    public void SetEditorMode(int mode)
    {
        if (mode is not (0 or 1) || Draft is not { } draft) return;
        lock (storageGate)
        {
            if (documents!.GetValueOrDefault(draft.Id) is { } saved)
            {
                WriteCurrentState(saved with { EditorMode = mode });
                Draft = draft with { EditorMode = mode };
            }
            else Draft = draft with { EditorMode = mode };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool SaveDraft()
    {
        if (!HasChanges || Draft is not { } draft) return false;
        Commit(draft);
        return true;
    }

    /// <summary>桌面便签卡直接保存指定便签（galaxy 便签卡编辑同一条数据、产生历史版本）。不改 Draft 的选中状态；
    /// 正编辑同一条时同步草稿内容，主窗口下次呈现时不回跳。内容未变化时不动 Updated、不产生版本。</summary>
    public bool SaveCardEdit(Guid id, string title, string body)
    {
        title ??= "";
        var normalized = NormalizeNewlines(body ?? "");
        if (title.Length > TitleLimit || normalized.Length > BodyLimit || normalized.Contains('\0')) throw new InvalidDataException();
        lock (storageGate)
        {
            if (documents!.GetValueOrDefault(id) is not { Recycled: false } note) return false;
            if (note.Title == title && note.Body == normalized) return true;
            var next = note with { Title = title, Body = normalized };
            CommitCore(next, select: false);
            if (Draft?.Id == id) Draft = next;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal void AddImage(byte[] png)
    {
        lock (storageGate)
        {
            if (Draft is not { } draft || draft.Images is { Length: >= ImageLimit }) throw new InvalidOperationException();
            using var input = new MemoryStream(png, writable: false);
            ValidateImage(input);
            if (IsLink(directory)) throw new InvalidDataException();
            Directory.CreateDirectory(ImagesDirectory);
            if (IsLink(ImagesDirectory)) throw new InvalidDataException();
            var id = Guid.NewGuid();
            var path = ImagePath(draft.Id, id);
            var temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(png);
                    stream.Flush(true);
                }
                File.Move(temporary, path);
            }
            finally { File.Delete(temporary); }
            Draft = draft with { Images = [.. (draft.Images ?? []), id] };
        }
    }

    internal void RemoveImage(Guid id)
    {
        lock (storageGate)
        {
            if (Draft is not { Images: { } images } draft || !images.Contains(id)) return;
            var remaining = images.Where(item => item != id).ToArray();
            Draft = draft with { Images = remaining.Length == 0 ? null : remaining };
            PruneImages(draft.Id);
        }
    }

    internal string ImagePath(Guid note, Guid image) => Path.Combine(ImagesDirectory, $"{note:N}-{image:N}.png");

    internal string? FindImage(Guid note, Guid image)
    {
        try
        {
            var path = ImagePath(note, image);
            if (IsLink(directory)) return null;
            using var input = ScreenshotStore.OpenSessionFile(ImagesDirectory, Path.GetFileName(path), ImageByteLimit);
            ValidateImage(input);
            return path;
        }
        catch (Exception error) when (IsStorageError(error)) { return null; }
    }

    internal static void ValidateImage(Stream input)
    {
        if (input.Length > ImageByteLimit) throw new InvalidDataException();
        var (width, height) = ScreenshotStore.ReadSize(input);
        if ((ulong)width * height > ImagePixelLimit) throw new InvalidDataException();
    }

    static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    internal static (string Title, string Body) ReadFile(string path)
    {
        if (!IsNoteFile(path)) throw new InvalidDataException();
        using var stream = File.OpenRead(path);
        if (stream.Length > (long)BodyLimit * 4 + 3) throw new InvalidDataException();
        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false);
        var buffer = new char[(BodyLimit + 1) * 2];
        int count = reader.ReadBlock(buffer);
        var body = NormalizeNewlines(new string(buffer, 0, count));
        if (body.Length > BodyLimit || body.Contains('\0') || count == buffer.Length) throw new InvalidDataException();
        var title = Path.GetFileNameWithoutExtension(path);
        if (title.Length > TitleLimit) title = title[..(char.IsHighSurrogate(title[TitleLimit - 1]) ? TitleLimit - 1 : TitleLimit)];
        return (title, body);
    }

    internal void ImportFile(string title, string body)
    {
        if (HasChanges) throw new InvalidOperationException();
        Commit(new(Guid.NewGuid(), title, body, default));
    }

    internal void SaveNew(string title, string body)
    {
        body = NormalizeNewlines(body);
        if (title.Length > TitleLimit || body.Length > BodyLimit || string.IsNullOrWhiteSpace(body) || body.Contains('\0'))
            throw new InvalidDataException();
        Load();
        Commit(new(Guid.NewGuid(), title, body, default), select: false);
    }

    internal void ExportFile(NoteDocument note, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        var parent = StorageLocation.ResolvePath(Path.GetDirectoryName(path)!);
        var name = Path.GetFileName(path);
        if (name.Contains(':') || !IsNoteFile(name) ||
            StorageLocation.Within(parent, StorageLocation.ResolvePath(directory))) throw new InvalidDataException();
        var temporary = Path.Combine(parent, $".pane-note-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream)) writer.Write(note.Body);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(parent, name), overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    static bool IsNoteFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".txt";

    public IReadOnlyList<DateTimeOffset> Versions(Guid id) => documents!.TryGetValue(id, out var saved)
        ? HistoryFiles(id).Select(Path.GetFileNameWithoutExtension).Select(name =>
            name is { Length: 52 } && long.TryParse(name.AsSpan(33), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ? ticks : -1)
            .Where(ticks => ticks >= 0 && ticks < saved.Updated.UtcTicks).OrderDescending().Take(VersionLimit)
            .Select(ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).ToArray()
        : [];

    public NoteDocument ReadVersion(Guid id, DateTimeOffset version)
    {
        if (!documents!.TryGetValue(id, out var saved) || version >= saved.Updated)
            throw new FileNotFoundException();
        using var stream = File.OpenRead(VersionPath(id, version));
        var note = JsonSerializer.Deserialize(stream, NoteJson.Default.NoteDocument);
        if (note == null || note.Recycled || note.Id != id || note.Updated != version || note.Title == null || note.Body == null || !ValidImages(note))
            throw new InvalidDataException();
        return note;
    }

    /// <summary>版本内容指纹（galaxy note.restore 的 expectedRevision 对齐）：预览时取指纹，
    /// 恢复时再验一次，期间版本文件被外部改动就拒绝，避免把预览到的内容换成别的东西。</summary>
    internal static string VersionHash(NoteDocument note)
    {
        var payload = Encoding.UTF8.GetBytes(note.Title + "\n" + note.Body);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
    }

    public bool RestoreVersion(Guid id, DateTimeOffset version, string? expectedHash = null)
    {
        if (Draft?.Id != id || HasChanges || !DraftIsSaved) return false;
        var previous = ReadVersion(id, version);
        if (expectedHash is { } expected && !string.Equals(expected, VersionHash(previous), StringComparison.Ordinal)) return false;
        if (!SameContent(previous, Draft)) Commit(previous);
        return true;
    }

    void Commit(NoteDocument draft, bool select = true)
    {
        lock (storageGate) CommitCore(draft, select);
        Changed?.Invoke(this,EventArgs.Empty);
    }

    void CommitCore(NoteDocument draft, bool select)
    {
        var loaded = documents!;
        var previous = loaded.GetValueOrDefault(draft.Id);
        var now = DateTimeOffset.UtcNow;
        if (previous != null && now <= previous.Updated)
        {
            if (previous.Updated == DateTimeOffset.MaxValue) throw new InvalidDataException();
            now = previous.Updated.ToUniversalTime().AddTicks(1);
        }
        var saved = draft with { Updated = now };
        foreach (var image in saved.Images ?? [])
            if (FindImage(saved.Id, image) == null) throw new InvalidDataException();
        if (previous != null)
        {
            Directory.CreateDirectory(HistoryDirectory);
            Write(previous, VersionPath(previous.Id, previous.Updated), overwrite: true);
        }
        Write(saved, PathFor(saved.Id), overwrite: previous != null);
        loaded[saved.Id] = saved;
        if (select) Draft = saved;
        if (previous != null) TrimVersions(saved.Id);
        if (previous?.Images != null || saved.Images != null || Directory.Exists(ImagesDirectory)) PruneImages(saved.Id);
    }

    void PruneImages(Guid id)
    {
        try
        {
            if (!Directory.Exists(ImagesDirectory) || IsLink(directory) || IsLink(ImagesDirectory)) return;
            var files = Directory.GetFiles(ImagesDirectory, $"{id:N}-*.png");
            if (files.Length == 0) return;
            var retained = new HashSet<Guid>();
            if (Draft?.Id == id) retained.UnionWith(Draft.Images ?? []);
            if (documents!.TryGetValue(id, out var current))
            {
                retained.UnionWith(current.Images ?? []);
                foreach (var version in Versions(id)) retained.UnionWith(ReadVersion(id, version).Images ?? []);
            }
            foreach (var path in files)
            {
                var leaf = Path.GetFileNameWithoutExtension(path);
                if (leaf.Length == 65 && Guid.TryParseExact(leaf.AsSpan(33), "N", out var image) && !retained.Contains(image) && !IsLink(path))
                    File.Delete(path);
            }
        }
        catch (Exception error) when (IsStorageError(error)) { }
    }

    void TrimVersions(Guid id)
    {
        try
        {
            foreach (var file in HistoryFiles(id).OrderDescending(StringComparer.Ordinal).Skip(VersionLimit))
                File.Delete(file);
        }
        catch (Exception error) when (IsStorageError(error)) { }
    }

    static void Write(NoteDocument note, string file, bool overwrite)
    {
        var temporary = file + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, note, NoteJson.Default.NoteDocument);
                stream.Flush(true);
            }
            File.Move(temporary, file, overwrite);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (IsStorageError(error)) { }
            throw;
        }
    }

    public void RecycleDraft()
    {
        lock (storageGate)
        {
            if (Draft is not { } draft) return;
            if (DraftIsSaved)
            {
                var recycled = draft with { Recycled = true };
                if (HasChanges) CommitCore(recycled, select: false);
                else WriteCurrentState(recycled);
            }
            Draft = null;
            PruneImages(draft.Id);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>回收任意已保存便签（不经过草稿，行菜单删除用）。草稿正编辑同一条且有未保存改动时返回 false。</summary>
    public bool Recycle(Guid id)
    {
        lock (storageGate)
        {
            if (documents!.GetValueOrDefault(id) is not { Recycled: false } note) return false;
            if (Draft?.Id == id && HasChanges) return false;
            WriteCurrentState(note with { Recycled = true });
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>分类改名：批量更新该分类下所有已保存便签。元数据级操作，不产生历史版本、不推进 Updated；
    /// 草稿正好是该分类时同步改草稿，保持已保存语义。</summary>
    public int RenameCategory(string from, string to)
    {
        var target = to?.Trim() ?? "";
        if (target.Length == 0 || target.Length > CategoryLimit || from.Length == 0) throw new ArgumentException(null, nameof(to));
        lock (storageGate)
        {
            var affected = 0;
            foreach (var (id, note) in documents!)
            {
                if (!string.Equals(note.Category, from, StringComparison.OrdinalIgnoreCase)) continue;
                WriteCurrentState(note with { Category = target });
                if (Draft?.Id == id) Draft = Draft with { Category = target };
                affected++;
            }
            if (affected > 0) Changed?.Invoke(this, EventArgs.Empty);
            return affected;
        }
    }

    /// <summary>删除分类：该分类下便签归为未分类（元数据级，语义同 RenameCategory）。</summary>
    public int ClearCategory(string name)
    {
        if (name.Length == 0) throw new ArgumentException(null, nameof(name));
        lock (storageGate)
        {
            var affected = 0;
            foreach (var (id, note) in documents!)
            {
                if (!string.Equals(note.Category, name, StringComparison.OrdinalIgnoreCase)) continue;
                WriteCurrentState(note with { Category = "" });
                if (Draft?.Id == id) Draft = Draft with { Category = "" };
                affected++;
            }
            if (affected > 0) Changed?.Invoke(this, EventArgs.Empty);
            return affected;
        }
    }

    /// <summary>移动任意已保存便签到分类（不经过草稿）。</summary>
    public bool MoveCategory(Guid id, string category)
    {
        var target = category?.Trim() ?? "";
        if (target.Length > CategoryLimit) return false;
        lock (storageGate)
        {
            if (documents!.GetValueOrDefault(id) is not { Recycled: false } note) return false;
            WriteCurrentState(note with { Category = target });
            if (Draft?.Id == id) Draft = Draft with { Category = target };
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool RestoreRecycled(Guid id)
    {
        lock (storageGate)
        {
            if (documents!.GetValueOrDefault(id) is not { Recycled: true } note) return false;
            WriteCurrentState(note with { Recycled = false });
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    void WriteCurrentState(NoteDocument note)
    {
        Write(note, PathFor(note.Id), overwrite: true);
        documents![note.Id] = note;
    }

    public bool DeleteRecycled(Guid id)
    {
        lock (storageGate)
        {
            var loaded = documents!;
            if (loaded.GetValueOrDefault(id) is not { Recycled: true }) return false;
            if (IsLink(directory) || Directory.Exists(HistoryDirectory) && IsLink(HistoryDirectory)) throw new InvalidDataException();
            var file = PathFor(id);
            File.Delete(file + ".tmp");
            foreach (var version in HistoryFiles(id, ".json.tmp")) File.Delete(version);
            foreach (var version in HistoryFiles(id)) File.Delete(version);
            File.Delete(file);
            loaded.Remove(id);
            PruneImages(id);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal string PathFor(Guid id) => Path.Combine(directory, $"{id:N}.json");

    internal string VersionPath(Guid id, DateTimeOffset version) => Path.Combine(HistoryDirectory,
        $"{id:N}-{version.UtcTicks.ToString("D19", CultureInfo.InvariantCulture)}.json");

    IEnumerable<string> HistoryFiles(Guid id, string extension = ".json") => Directory.Exists(HistoryDirectory)
        ? Directory.EnumerateFiles(HistoryDirectory, $"{id:N}-???????????????????{extension}") : [];

    static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;
}
