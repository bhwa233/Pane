using System.IO.Compression;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Clipboard;
using Pane.Services.Screenshots;
using Pane.Settings;

static partial class Program
{
    static async Task OcrHistoryBackupTests()
    {
        var root = TestResults.NewDirectory("ocr-history-backup-");
        var source = new SettingsStore(Path.Combine(root, "source"), Defaults());
        var target = new SettingsStore(Path.Combine(root, "target"), Defaults());
        var sourceStore = OpenHistory(source);
        var targetStore = OpenHistory(target);
        var original = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC");
        const string recognitionJson = """{"width":1,"height":1,"text":"Pane OCR","lines":[{"text":"Pane OCR","box":[[0,0],[1,0],[1,1],[0,1]]}]}""";
        var payload = System.Text.Encoding.UTF8.GetBytes(recognitionJson);
        using var document = JsonDocument.Parse(payload);
        var sample = new OcrImageResult(NativeScreenshotOcr.ReadRecognition(document.RootElement, (1, 1)), original, payload);
        var archivedWithEdits = sourceStore.Save(sample, "First edit");
        sourceStore.Revise(archivedWithEdits, "Saved edit");
        var archivedOriginal = sourceStore.Save(sample, "Pane OCR");
        File.SetCreationTimeUtc(ResultPath(source, archivedOriginal.Id), new DateTime(2025, 2, 1, 8, 0, 0, DateTimeKind.Utc));
        File.SetCreationTimeUtc(ResultPath(source, archivedWithEdits.Id), new DateTime(2025, 2, 2, 8, 0, 0, DateTimeKind.Utc));
        var session = Directory.CreateDirectory(Path.Combine(source.DirectoryPath, "ocr-sessions"));
        File.WriteAllText(Path.Combine(session.FullName, "ocr.json"), "unsaved work");
        var archive = Path.Combine(root, "archive.zip");
        var export = new DataStorage(source, () => null, () => { });
        var transfer = new DataStorage(target, () => null, () => { });
        await export.ExportAsync(archive, new(false, false, ClipboardExport.TextOnly, Screenshots: true, OcrHistory: true), null, default);
        using (var zip = ZipFile.OpenRead(archive))
            Check(!zip.Entries.Any(entry => entry.FullName.StartsWith("ocr-sessions/", StringComparison.Ordinal)), "Unsaved recognition session is excluded");
        Check(await transfer.InspectAsync(archive, default) is { HasSettings: false, OcrHistory: 2, Screenshots: 0 }, "Mixed backup exposes OCR independently of screenshots and settings");

        var localOnly = targetStore.Save(sample, "Local record");
        await transfer.StageImportAsync(archive, new(false, false, true, DataImportMode.Replace), null, default);
        LoadApplied();
        Check(targetStore.List().Select(entry => entry.Id).SequenceEqual([localOnly.Id]), "Import without OCR selected leaves the local archive untouched");
        await Stage(archive, DataImportMode.Merge);
        Check(targetStore.List().Select(entry => entry.Id).SequenceEqual([localOnly.Id]), "Staging does not modify the live archive");
        LoadApplied();
        var (restored, image) = targetStore.Read(archivedWithEdits.Id);
        Check(targetStore.List().Where(entry => entry.Id != localOnly.Id).SequenceEqual(sourceStore.List()) && image.SequenceEqual(original) &&
            restored.Original.Hit(new(.5, .5)) is { Start: 0, Length: 8 } && restored.Corrections.Select(edit => edit.Text).SequenceEqual(["Saved edit", "First edit"]),
            "Import restores the source image, recognition geometry, correction history and order");

        targetStore.Revise(targetStore.Read(archivedOriginal.Id).Record, "Local edit");
        await Stage(archive, DataImportMode.Merge);
        LoadApplied();
        Check(targetStore.Read(archivedOriginal.Id).Record.Text == "Local edit" && targetStore.Read(localOnly.Id).Record.Text == "Local record",
            "Merge preserves same-ID edits and unrelated local records");

        var malformed = Path.Combine(root, "mismatched-image.zip");
        File.Copy(archive, malformed);
        using (var zip = ZipFile.Open(malformed, ZipArchiveMode.Update))
        {
            var name = $"ocr-history/{archivedOriginal.Id:N}/ocr.json";
            zip.GetEntry(name)!.Delete();
            using var result = new StreamWriter(zip.CreateEntry(name).Open());
            result.Write(recognitionJson.Replace("\"width\":1", "\"width\":2", StringComparison.Ordinal));
        }
        try { await Stage(malformed, DataImportMode.Replace); Check(false, "Mismatched recognition dimensions must be rejected"); }
        catch (DataStorageException)
        {
            target.Load();
            Check(targetStore.Read(archivedOriginal.Id).Record.Text == "Local edit", "Rejected replacement leaves the live archive intact on the next launch");
        }

        await Stage(archive, DataImportMode.Replace);
        LoadApplied();
        var automaticBackup = transfer.LastImport!.Backup;
        Check(targetStore.List().SequenceEqual(sourceStore.List()) && targetStore.Read(archivedOriginal.Id).Record.Corrections.Length == 0,
            "Replace drops local-only records and clears corrections omitted by the selected backup");
        await Stage(automaticBackup, DataImportMode.Replace);
        LoadApplied();
        Check(targetStore.Read(archivedOriginal.Id).Record.Text == "Local edit" && targetStore.Read(localOnly.Id).Record.Text == "Local record",
            "Existing pre-import backup includes the previous OCR archive");

        Task Stage(string path, DataImportMode mode) => transfer.StageImportAsync(path, new(false, false, false, mode, OcrHistory: true), null, default);
        void LoadApplied()
        {
            target.Load();
            Check(transfer.LastImport is { Error: null }, transfer.LastImport?.Error ?? "Missing import outcome");
        }
        static OcrHistoryStore OpenHistory(SettingsStore settings) => new(Path.Combine(settings.DirectoryPath, OcrHistoryStore.DirectoryName), settings.StorageSync);
        static string ResultPath(SettingsStore settings, Guid id) => Path.Combine(settings.DirectoryPath, OcrHistoryStore.DirectoryName, id.ToString("N"), OcrHistoryStore.ResultName);
    }
}
