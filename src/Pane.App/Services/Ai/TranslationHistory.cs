using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pane.Services.Ai;

internal sealed record TranslationHistoryEntry(string Source, string? Target = null, string? Result = null);

[JsonSerializable(typeof(TranslationHistoryEntry[]))]
internal sealed partial class TranslationHistoryJson : JsonSerializerContext { }

/// <summary>译文历史（2026-11-22 起仅翻译一类）：最近 200 条、单个文件 2 MiB 上限（沿用既有约定）。
/// 默认关闭（RememberTranslations），开启后翻译完成自动记一条；删除单条 / 清空不删文件。</summary>
internal sealed class TranslationHistory(string directory)
{
    const long MaxBytes = 2 * 1024 * 1024;
    const int Limit = 200;
    readonly string path = Path.Combine(directory, "translation-history.json");
    TranslationHistoryEntry[]? entries;

    internal IReadOnlyList<TranslationHistoryEntry> Read()
    {
        if (entries != null) return entries;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxBytes) throw new InvalidDataException();
            var stored = JsonSerializer.Deserialize(stream, TranslationHistoryJson.Default.TranslationHistoryEntryArray);
            if (stored == null || stored.Length > Limit || stored.Any(entry => !Valid(entry))) throw new InvalidDataException();
            return entries = stored;
        }
        catch (FileNotFoundException) { return entries = []; }
    }

    internal bool Record(TranslationHistoryEntry entry)
    {
        if (!Valid(entry) || EstimateBytes(entry) > MaxBytes - 2) return false;
        var current = Read();
        var next = new List<TranslationHistoryEntry>(Limit) { entry };
        var bytes = 2 + EstimateBytes(entry);
        foreach (var older in current)
        {
            if (older.Source.Equals(entry.Source, StringComparison.Ordinal) && older.Target == entry.Target) continue;
            bytes += EstimateBytes(older);
            if (next.Count == Limit || bytes > MaxBytes) break;
            next.Add(older);
        }
        if (!next.SequenceEqual(current)) Save([.. next]);
        return true;
    }

    internal void Remove(TranslationHistoryEntry entry) => Save(Read().Where(item => item != entry).ToArray());

    internal void Clear()
    {
        if (Read().Count != 0) Save([]);
    }

    bool Valid(TranslationHistoryEntry? entry) => entry != null && !string.IsNullOrWhiteSpace(entry.Source) &&
        !string.IsNullOrWhiteSpace(entry.Target) && !string.IsNullOrWhiteSpace(entry.Result);

    static long EstimateBytes(TranslationHistoryEntry entry) =>
        128 + 6L * (entry.Source.Length + (long)(entry.Target?.Length ?? 0) + (entry.Result?.Length ?? 0));

    void Save(TranslationHistoryEntry[] next)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(next, TranslationHistoryJson.Default.TranslationHistoryEntryArray);
        if (data.Length > MaxBytes) throw new InvalidDataException();
        File.WriteAllBytes(path + ".tmp", data);
        File.Move(path + ".tmp", path, overwrite: true);
        entries = next;
    }

    internal static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException;
}
