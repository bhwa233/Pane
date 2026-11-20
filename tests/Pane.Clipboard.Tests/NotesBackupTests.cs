using System.IO.Compression;
using System.Text.Json.Nodes;
using Pane.Contracts;
using Pane.Services.Clipboard;
using Pane.Services.Notes;
using Pane.Settings;

static partial class Program
{
    static async Task NotesBackupTests()
    {
        var root = TestResults.NewDirectory("notes-backup-");
        Directory.CreateDirectory(root);
        var source = new SettingsStore(Path.Combine(root, "source"), Defaults());
        var sourceNotes = OpenNotes(source);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC");
        sourceNotes.Select(null);
        sourceNotes.Edit("Release", "Original", "Work");
        sourceNotes.AddImage(png);
        sourceNotes.SaveDraft();
        var id = sourceNotes.Draft!.Id;
        var version = sourceNotes.Draft.Updated;
        var image = sourceNotes.Draft.Images!.Single();
        sourceNotes.RemoveImage(image);
        sourceNotes.Edit("Release", "Published", "Work");
        sourceNotes.SaveDraft();
        var anchor = new LauncherPosition("fixture-source", .2, .6);
        var reminderTime = DateTimeOffset.UnixEpoch.AddDays(1);
        source.Save(NoteReminderSchedule.Set(source.Current with { DesktopNotes = new() { [id] = anchor } }, id, "Source task", reminderTime));
        sourceNotes.Edit("Unsaved", "Do not include the draft");
        var archive = Path.Combine(root, "notes.zip");
        await Transfer(source).ExportAsync(archive, new(false, false, ClipboardExport.None, Notes: true), null, default);
        Check(await Transfer(source).InspectAsync(archive, default) is { HasSettings: false, Notes: 1 }, "Notes-only archive reports its records without including general settings");

        var target = new SettingsStore(Path.Combine(root, "target"), Defaults());
        await Import(target, archive, Notes(DataImportMode.Replace));
        var restored = OpenNotes(target);
        Check(restored.Find(id) is { Body: "Published", Category: "Work" } && restored.ReadVersion(id, version).Body == "Original" &&
            target.Current.DesktopNotes?[id] == anchor, "Restores saved content, its real prior version, and the card anchor into an empty profile");
        Check(restored.Find(id)?.Images == null && restored.ReadVersion(id, version).Images!.Single() == image &&
            restored.FindImage(id, image) is { } imagePath && File.ReadAllBytes(imagePath).SequenceEqual(png),
            "Restores a managed image referenced only by an archived version, with its original bytes");

        restored.Select(id);
        restored.Edit("Local", "Keep my changes");
        restored.SaveDraft();
        var localAnchor = new LauncherPosition("fixture-target", .5, .4);
        var screenshotId = Guid.NewGuid();
        target.Save(NoteReminderSchedule.Set(target.Current with
        {
            DesktopNotes = new() { [id] = localAnchor }, DesktopScreenshots = new() { [screenshotId] = localAnchor }, DesktopTraffic = new(localAnchor),
        }, id, "Local task", reminderTime.AddDays(1)));
        await Import(target, archive, Notes(DataImportMode.Merge));
        Check(OpenNotes(target).Find(id)?.Body == "Keep my changes" && target.Current.DesktopNotes?[id] == localAnchor,
            "Merging the same note id leaves the target content, history, and layout intact");

        var settingsArchive = Path.Combine(root, "settings.zip");
        await Transfer(source).ExportAsync(settingsArchive, new(true, false, ClipboardExport.None), null, default);
        using (var zip = ZipFile.Open(settingsArchive, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("settings/settings.json")!;
            using (var reader = new StreamReader(entry.Open()))
            {
                var portableSettings = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
                Check(!portableSettings.ContainsKey("DesktopNotes") && !portableSettings.ContainsKey("NoteTaskReminders"),
                    "Settings export excludes local note pins and task reminders");
            }
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("settings/settings.json").Open());
            writer.Write(File.ReadAllText(Path.Combine(source.DirectoryPath, "settings.json")));
        }
        await Import(target, settingsArchive, new(true, false, false, DataImportMode.Replace));
        Check(target.Current.DesktopNotes?[id] == localAnchor && target.Current.DesktopScreenshots?[screenshotId] == localAnchor &&
            target.Current.DesktopTraffic?.Position == localAnchor, "Legacy settings-only imports preserve all three local card layouts");
        Check(NoteReminderSchedule.Next(target.Current) == new NoteReminder(id, "Local task", reminderTime.AddDays(1)) &&
            target.Current.NoteTaskReminders?[id].Count == 1, "Settings import neither overwrites local reminders nor schedules foreign tasks");

        var notePath = restored.PathFor(id);
        var before = new[] { notePath, restored.VersionPath(id, version), restored.ImagePath(id, image), Path.Combine(target.DirectoryPath, "settings.json") }.ToDictionary(path => path, File.ReadAllBytes);
        var invalid = Path.Combine(root, "wrong-note.zip");
        File.Copy(archive, invalid);
        using (var zip = ZipFile.Open(invalid, ZipArchiveMode.Update))
        {
            zip.GetEntry("notes/index.json")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("notes/index.json").Open());
            writer.Write(new JsonObject { ["count"] = 1, ["versions"] = 1, ["images"] = 1, ["pins"] = new JsonObject { [Guid.NewGuid().ToString("D")] = null } }.ToJsonString());
        }
        try { await Transfer(target).StageImportAsync(invalid, Notes(DataImportMode.Replace), null, default); Check(false, "Missing pin target should reject the archive"); }
        catch (DataStorageException) { Check(Unchanged(), "Invalid pin target fails staging without writing live files"); }

        var missingHistory = Path.Combine(root, "missing-history.zip");
        File.Copy(archive, missingHistory);
        using (var zip = ZipFile.Open(missingHistory, ZipArchiveMode.Update))
            zip.Entries.Single(entry => entry.FullName.StartsWith("notes/.history/", StringComparison.Ordinal)).Delete();
        try { await Transfer(target).StageImportAsync(missingHistory, Notes(DataImportMode.Replace), null, default); Check(false, "Missing history file should reject the archive"); }
        catch (DataStorageException) { Check(Unchanged(), "Incomplete history fails staging without writing live files"); }

        var missingImage = Path.Combine(root, "missing-image.zip");
        File.Copy(archive, missingImage);
        using (var zip = ZipFile.Open(missingImage, ZipArchiveMode.Update))
            zip.Entries.Single(entry => entry.FullName.StartsWith("notes/.images/", StringComparison.Ordinal)).Delete();
        try { await Transfer(target).StageImportAsync(missingImage, Notes(DataImportMode.Replace), null, default); Check(false, "Missing image should reject the archive"); }
        catch (DataStorageException) { Check(Unchanged(), "Missing image fails staging without changing the target"); }

        await Transfer(target).StageImportAsync(archive, Notes(DataImportMode.Replace), null, default);
        DataStorage.ImportStep = step => { if (step == "applied-file") throw new IOException("inject failure after first note mutation"); };
        try { StorageRecovery.RecoverAll(target.DirectoryPath); }
        finally { DataStorage.ImportStep = null; }
        Check(Transfer(target).LastImport?.Error != null && Unchanged(), "A failed write restores note, history, and config bytes");

        await Import(target, archive, Notes(DataImportMode.Replace));
        var previous = Transfer(target).LastImport!.Backup;
        Check(OpenNotes(target).Find(id)?.Body == "Published", "Explicit replace restores the selected backup content");
        await Import(target, previous, Notes(DataImportMode.Replace));
        Check(OpenNotes(target).Find(id)?.Body == "Keep my changes" && target.Current.DesktopNotes?[id] == localAnchor,
            "Automatic pre-import archive restores the overwritten local note and anchor");
        sourceNotes.Edit("Release", "Published", "Work");
        sourceNotes.RecycleDraft();
        var recycledArchive = Path.Combine(root, "recycled-notes.zip");
        await Transfer(source).ExportAsync(recycledArchive, new(false, false, ClipboardExport.None, Notes: true), null, default);
        await Import(target, recycledArchive, Notes(DataImportMode.Replace));
        restored = OpenNotes(target);
        Check(restored.Find(id) == null && restored.RecycledDocuments.Single().Id == id && target.Current.DesktopNotes?.ContainsKey(id) != true &&
            restored.RestoreRecycled(id) && restored.Find(id)?.Body == "Published" && restored.ReadVersion(id, version).Body == "Original" &&
            restored.FindImage(id, image) is { } recycledImage && File.ReadAllBytes(recycledImage).SequenceEqual(png),
            "Backup preserves recycled status, history, and attachment bytes without restoring a stale pin");
        Console.WriteLine($"PASS note backup assertions={checks}");

        bool Unchanged() => before.All(pair => File.Exists(pair.Key) && pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key)));
        static DataStorage Transfer(SettingsStore settings) => new(settings, () => null, () => { });
        static DataImportOptions Notes(DataImportMode mode) => new(false, false, false, mode, Notes: true);
        static NoteStore OpenNotes(SettingsStore settings)
        {
            var store = new NoteStore(Path.Combine(settings.DirectoryPath, "notes"), settings.StorageSync);
            store.Load();
            return store;
        }
        static async Task Import(SettingsStore settings, string archive, DataImportOptions options)
        {
            var transfer = Transfer(settings);
            await transfer.StageImportAsync(archive, options, null, default);
            settings.Load();
            if (transfer.LastImport is not { Error: null }) throw new InvalidOperationException(transfer.LastImport?.Error);
        }
    }
}
