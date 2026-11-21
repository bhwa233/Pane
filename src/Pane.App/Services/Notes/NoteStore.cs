using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pane.Services.Clipboard;

namespace Pane.Services.Notes;

/// <summary>纯文本便签（notes-mvp §1）：只保留标题、正文、分类、时间与回收状态。
/// 旧记录里的 Images/EditorMode 读取时忽略，下次提交写出 MVP 模型后不再保留。</summary>
internal sealed record NoteDocument(Guid Id, string Title, string Body, DateTimeOffset Updated)
{
    string category = "";
    public string Category { get => category; init => category = value?.Trim() ?? ""; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Recycled { get; init; }
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
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
    string HistoryDirectory => Path.Combine(directory, ".history");
    Dictionary<Guid, NoteDocument>? documents;
    public int UnreadableCount { get; private set; }
    public event EventHandler? Changed;

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
                if (note == null || note.Id != id || note.Title == null || note.Body == null) unreadable++;
                else loaded.Add(id, note);
            }
            catch (Exception error) when (IsStorageError(error)) { unreadable++; }
        }
        documents = loaded;
        UnreadableCount = unreadable;
    }

    /// <summary>新建一条独立便签（导入 / 工具结果落盘 / 会话首次落盘）。不依赖任何编辑会话，
    /// 永不因别处未保存而拒绝。id 缺省时生成新 GUID；指定时必须与现有便签不冲突。</summary>
    public NoteDocument Create(string title, string body, string? category = null, Guid? id = null)
    {
        var normalized = NormalizeNewlines(body ?? "");
        if ((title ?? "").Length > TitleLimit || normalized.Length > BodyLimit || (title ?? "").Contains('\0') || normalized.Contains('\0'))
            throw new InvalidDataException();
        var saved = new NoteDocument(id ?? Guid.NewGuid(), title ?? "", normalized, default) { Category = category?.Trim() ?? "" };
        lock (storageGate)
        {
            if (documents!.ContainsKey(saved.Id)) throw new InvalidDataException();
            Write(saved, PathFor(saved.Id), overwrite: false);
            documents[saved.Id] = saved;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    /// <summary>提交已存在便签的当前正文。history 为真时先把被替换内容记为一个历史版本；
    /// 自动保存传 false，避免连续输入挤满 30 版上限（notes-mvp §4）。
    /// 内容未变化时不写盘、不推进 Updated、不产生版本。</summary>
    public NoteDocument Save(NoteDocument note, bool history)
    {
        var normalized = NormalizeNewlines(note.Body ?? "");
        if ((note.Title ?? "").Length > TitleLimit || normalized.Length > BodyLimit || (note.Title ?? "").Contains('\0') || normalized.Contains('\0'))
            throw new InvalidDataException();
        lock (storageGate)
        {
            var previous = documents!.GetValueOrDefault(note.Id);
            if (previous is not { Recycled: false } current) throw new KeyNotFoundException();
            var next = note with { Title = note.Title ?? "", Body = normalized, Category = note.Category?.Trim() ?? "" };
            if (SameContent(next, current)) return current;
            next = next with { Updated = NextStamp(current.Updated) };
            if (history) WriteHistory(current);
            Write(next, PathFor(next.Id), overwrite: true);
            documents![next.Id] = next;
            if (history) TrimVersions(next.Id);
            return next;
        }
    }

    /// <summary>恢复历史版本：被替换的当前内容先记为历史，恢复后的便签保留当前分类，
    /// 不被旧正文的分类带回（notes-mvp §4）。</summary>
    public NoteDocument Restore(Guid id, NoteDocument version)
    {
        lock (storageGate)
        {
            var current = documents!.GetValueOrDefault(id);
            if (current is not { Recycled: false } saved) throw new KeyNotFoundException();
            var restored = saved with { Title = version.Title, Body = version.Body, Updated = NextStamp(saved.Updated) };
            WriteHistory(saved);
            Write(restored, PathFor(id), overwrite: true);
            documents![id] = restored;
            TrimVersions(id);
            return restored;
        }
    }

    static bool SameContent(NoteDocument first, NoteDocument second) =>
        first.Title == second.Title && first.Body == second.Body && first.Category == second.Category;

    static DateTimeOffset NextStamp(DateTimeOffset previous)
    {
        var now = DateTimeOffset.UtcNow;
        if (now <= previous)
        {
            if (previous == DateTimeOffset.MaxValue) throw new InvalidDataException();
            now = previous.ToUniversalTime().AddTicks(1);
        }
        return now;
    }

    void WriteHistory(NoteDocument previous)
    {
        Directory.CreateDirectory(HistoryDirectory);
        Write(previous, VersionPath(previous.Id, previous.Updated), overwrite: true);
    }

    /// <summary>TXT 读取（notes-mvp §3）：严格 UTF-8、容量与换行规范化。选择器和存储入口都拒绝非 .txt。</summary>
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

    /// <summary>另存建议名：标题 → 正文首个非空行 → fallback。替换 Windows 非法字符和保留名，
    /// 去掉首尾空格与点并限制长度（notes-mvp §3）。</summary>
    internal static string SuggestedFileName(string title, string body, string fallback)
    {
        var source = (title ?? "").Trim();
        if (source.Length == 0)
            foreach (var line in (body ?? "").Replace("\r", "").Split('\n'))
            {
                source = line.Trim();
                if (source.Length > 0) break;
            }
        if (source.Length == 0) source = fallback;
        var builder = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            if (character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(character)) continue;
            builder.Append(character);
        }
        var name = builder.ToString().Trim(' ', '.');
        if (name.Length == 0) name = fallback;
        var stem = name.Split('.')[0].Trim();
        if (stem.Length > 0 && ReservedNames.Contains(stem.ToLowerInvariant())) name = "_" + name;
        if (name.Length > 80) name = name[..(char.IsHighSurrogate(name[79]) ? 78 : 80)].TrimEnd(' ', '.');
        return name.Length == 0 ? fallback : name;
    }

    static readonly HashSet<string> ReservedNames = ["con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"];

    /// <summary>TXT 另存：只生成 .txt，写当前纯文本正文，覆盖由保存对话框确认；
    /// 取消或写入失败不改变源便签（notes-mvp §3）。</summary>
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

    static bool IsNoteFile(string path) => Path.GetExtension(path).ToLowerInvariant() == ".txt";

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
        if (note == null || note.Recycled || note.Id != id || note.Updated != version || note.Title == null || note.Body == null)
            throw new InvalidDataException();
        return note;
    }

    /// <summary>版本内容指纹（galaxy note.restore 的 expectedRevision 对齐）：预览时取指纹，
    /// 恢复时再验一次，期间内容变化就拒绝，避免把预览到的内容换成别的东西。</summary>
    internal static string VersionHash(NoteDocument note)
    {
        var payload = Encoding.UTF8.GetBytes(note.Title + "\n" + note.Body);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
    }

    /// <summary>回收任意已保存便签（行菜单 / 编辑器删除用）。会话层负责先结清草稿。</summary>
    public bool Recycle(Guid id)
    {
        lock (storageGate)
        {
            if (documents!.GetValueOrDefault(id) is not { Recycled: false } note) return false;
            WriteCurrentState(note with { Recycled = true });
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>分类改名：批量更新该分类下所有已保存便签。元数据级操作，不产生历史版本、不推进 Updated。</summary>
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
                affected++;
            }
            if (affected > 0) Changed?.Invoke(this, EventArgs.Empty);
            return affected;
        }
    }

    /// <summary>移动任意已保存便签到分类。</summary>
    public bool MoveCategory(Guid id, string category)
    {
        var target = category?.Trim() ?? "";
        if (target.Length > CategoryLimit) return false;
        lock (storageGate)
        {
            if (documents!.GetValueOrDefault(id) is not { Recycled: false } note) return false;
            WriteCurrentState(note with { Category = target });
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
            var file = PathFor(id);
            File.Delete(file + ".tmp");
            foreach (var version in HistoryFiles(id, ".json.tmp")) File.Delete(version);
            foreach (var version in HistoryFiles(id)) File.Delete(version);
            File.Delete(file);
            loaded.Remove(id);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal string PathFor(Guid id) => Path.Combine(directory, $"{id:N}.json");

    internal string VersionPath(Guid id, DateTimeOffset version) => Path.Combine(HistoryDirectory,
        $"{id:N}-{version.UtcTicks.ToString("D19", CultureInfo.InvariantCulture)}.json");

    IEnumerable<string> HistoryFiles(Guid id, string extension = ".json") => Directory.Exists(HistoryDirectory)
        ? Directory.EnumerateFiles(HistoryDirectory, $"{id:N}-???????????????????{extension}") : [];

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

    static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;
}
