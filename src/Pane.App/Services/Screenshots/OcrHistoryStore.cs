using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pane.Services.Screenshots;

internal sealed record OcrHistoryEntry(Guid Id, DateTime CreatedUtc);
internal sealed record OcrCorrection(DateTimeOffset Saved, string Text);
internal sealed record SavedOcr(Guid Id, OcrRecognition Original, OcrCorrection[] Corrections)
{
    internal string Text => Corrections.FirstOrDefault()?.Text ?? Original.Text;
}

[JsonSerializable(typeof(OcrCorrection[]))]
internal sealed partial class OcrHistoryJson : JsonSerializerContext { }

internal sealed class OcrHistoryStore(string directory, object? storageSync = null)
{
    internal const int RecordLimit = 50;
    const int CorrectionLimit = 10;
    const string DeletedSuffix = ".deleted";
    internal const int MaxCorrectionBytes = 16 * 1024 * 1024;
    internal const string DirectoryName = "ocr-history", ImageName = "image.png", ResultName = "ocr.json", CorrectionsName = "corrections.json";
    readonly object gate = storageSync ?? new();

    internal OcrHistoryEntry[] List()
    {
        if (!Directory.Exists(directory)) return [];
        CheckDirectory(directory);
        return new DirectoryInfo(directory).EnumerateDirectories()
            .Where(record => (record.Attributes & FileAttributes.ReparsePoint) == 0 && File.Exists(Path.Combine(record.FullName, ResultName)))
            .Select(record => Guid.TryParseExact(record.Name, "N", out var id) && id != Guid.Empty
                ? new OcrHistoryEntry(id, File.GetCreationTimeUtc(Path.Combine(record.FullName, ResultName))) : null)
            .OfType<OcrHistoryEntry>().OrderByDescending(record => record.CreatedUtc).ThenBy(record => record.Id).ToArray();
    }

    internal (SavedOcr Record, byte[] Image) Read(Guid id)
    {
        CheckDirectory(directory);
        var folder = Folder(id);
        using var input = ScreenshotStore.OpenSessionFile(folder, ImageName, ScreenshotStore.MaxFileBytes);
        var record = ReadRecord(folder, id, input);
        input.Position = 0;
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return (record, bytes);
    }

    internal static void ValidateRecord(string folder, Guid id)
    {
        using var input = ScreenshotStore.OpenSessionFile(folder, ImageName, ScreenshotStore.MaxFileBytes);
        _ = ReadRecord(folder, id, input);
    }

    static SavedOcr ReadRecord(string folder, Guid id, Stream input)
    {
        var size = ScreenshotStore.ReadSize(input);
        if ((ulong)size.Width * size.Height > ScreenshotStore.MaxOcrPixels) throw new InvalidDataException("OCR image is too large.");
        using var result = ScreenshotStore.OpenSessionFile(folder, ResultName, NativeScreenshotOcr.MaxResultBytes);
        using var document = JsonDocument.Parse(result);
        var original = NativeScreenshotOcr.ReadRecognition(document.RootElement, size);
        return new(id, original, ReadCorrections(folder));
    }

    internal SavedOcr Save(OcrImageResult result, string text)
    {
        text = Normalize(text);
        using (var image = new MemoryStream(result.Image, writable: false))
            if (ScreenshotStore.ReadSize(image) != (result.Recognition.Width, result.Recognition.Height)) throw new InvalidDataException();
        if (result.Payload.Length > NativeScreenshotOcr.MaxResultBytes) throw new InvalidDataException();
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            if (List().Length >= RecordLimit) throw new InvalidOperationException("OCR history is full.");
            var id = Guid.NewGuid();
            var folder = Folder(id);
            var temporary = folder + ".tmp";
            var corrections = text == result.Recognition.Text ? Array.Empty<OcrCorrection>() : [new(DateTimeOffset.UtcNow, text)];
            Directory.CreateDirectory(temporary);
            try
            {
                WriteFile(Path.Combine(temporary, ImageName), result.Image);
                WriteFile(Path.Combine(temporary, ResultName), result.Payload);
                if (corrections.Length != 0) WriteCorrections(temporary, corrections);
                Directory.Move(temporary, folder);
            }
            finally { if (Directory.Exists(temporary)) RemoveFiles(temporary); }
            return new(id, result.Recognition, corrections);
        }
    }

    internal SavedOcr Revise(SavedOcr record, string text)
    {
        text = Normalize(text);
        lock (gate)
        {
            CheckDirectory(directory);
            var folder = Folder(record.Id);
            CheckDirectory(folder);
            var current = ReadCorrections(folder);
            if ((current.FirstOrDefault()?.Text ?? record.Original.Text) == text) return record with { Corrections = current };
            OcrCorrection[] next = [new(DateTimeOffset.UtcNow, text), .. current.Take(CorrectionLimit - 1)];
            WriteCorrections(folder, next);
            return record with { Corrections = next };
        }
    }

    internal void Delete(Guid id)
    {
        lock (gate)
        {
            CheckDirectory(directory);
            var folder = Folder(id);
            CheckDirectory(folder);
            if (new DirectoryInfo(folder).EnumerateFiles().Any(file => (file.Attributes & FileAttributes.ReadOnly) != 0))
                throw new UnauthorizedAccessException("An OCR history file is read-only.");
            var deleted = Path.Combine(directory, Guid.NewGuid().ToString("N") + DeletedSuffix);
            Directory.Move(folder, deleted);
            RemoveFiles(deleted);
        }
    }

    string Folder(Guid id) => id == Guid.Empty ? throw new InvalidDataException() : Path.Combine(directory, id.ToString("N"));

    static string Normalize(string text)
    {
        text = text.ReplaceLineEndings("\n");
        return text.Length <= NativeScreenshotOcr.MaxTextLength && !text.Contains('\0') ? text : throw new InvalidDataException();
    }

    static void CheckDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
    }

    static OcrCorrection[] ReadCorrections(string folder)
    {
        if (!File.Exists(Path.Combine(folder, CorrectionsName))) return [];
        using var input = ScreenshotStore.OpenSessionFile(folder, CorrectionsName, MaxCorrectionBytes);
        var corrections = JsonSerializer.Deserialize(input, OcrHistoryJson.Default.OcrCorrectionArray);
        return corrections is { Length: <= CorrectionLimit } && corrections.All(item => item?.Text is { } text && text == Normalize(text))
            ? corrections : throw new InvalidDataException("Invalid OCR correction history.");
    }

    static void WriteCorrections(string folder, OcrCorrection[] corrections)
    {
        var path = Path.Combine(folder, CorrectionsName);
        var temporary = path + ".tmp";
        try
        {
            WriteFile(temporary, JsonSerializer.SerializeToUtf8Bytes(corrections, OcrHistoryJson.Default.OcrCorrectionArray));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    static void WriteFile(string path, byte[] data)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(data);
        output.Flush(true);
    }

    static void RemoveFiles(string path)
    {
        foreach (var name in new[] { ImageName, ResultName, CorrectionsName, CorrectionsName + ".tmp" }) File.Delete(Path.Combine(path, name));
        Directory.Delete(path);
    }
}
