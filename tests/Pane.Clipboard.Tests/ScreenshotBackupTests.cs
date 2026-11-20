using System.IO.Compression;
using Pane.Contracts;
using Pane.Services.Clipboard;
using Pane.Services.Screenshots;
using Pane.Settings;

static partial class Program
{
    static async Task ScreenshotBackupTests()
    {
        var root = TestResults.NewDirectory("screenshot-backup-");
        var source = new SettingsStore(Path.Combine(root, "source"), Defaults());
        var target = new SettingsStore(Path.Combine(root, "target"), Defaults());
        var store = OpenScreenshots(source);
        var targetStore = OpenScreenshots(target);
        var session = Directory.CreateDirectory(Path.Combine(root, "session")).FullName;
        var original = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC");
        var rendered = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY2BgYPgPAAEEAQBwIGULAAAAAElFTkSuQmCC");
        const string blank = """{"format":"spark233-pin","version":1,"width":1,"height":1,"scale":1,"shapes":[]}""";
        const string annotated = """{"format":"spark233-pin","version":1,"width":1,"height":1,"scale":2,"shapes":[]}""";
        File.WriteAllBytes(Path.Combine(session, "pin-source.png"), original);
        WriteEdit(original, blank);
        var editable = store.ImportEditable(session, (1, 1));
        var plain = store.Import(Path.Combine(session, "pin-source.png"));
        File.SetCreationTimeUtc(store.PathFor(editable), new DateTime(2025, 6, 10, 8, 0, 0, DateTimeKind.Utc));
        File.SetCreationTimeUtc(store.PathFor(plain), new DateTime(2025, 6, 9, 9, 0, 0, DateTimeKind.Utc));
        WriteEdit(rendered, annotated);
        Publish(store);
        var expected = store.List();
        var sourceAnchor = new LauncherPosition("source-monitor", .2, .4);
        source.Save(source.Current with { DesktopScreenshots = new() { [editable] = sourceAnchor } });
        var archive = Path.Combine(root, "screenshots.zip");
        var export = new DataStorage(source, () => null, () => { });
        await export.ExportAsync(archive, new(false, false, ClipboardExport.TextOnly, Screenshots: true), null, default);
        using (var zip = ZipFile.OpenRead(archive))
            Check(zip.Entries.Count(entry => entry.FullName.Contains($"{editable:N}.pin/")) == 3, "Archive contains only current render, original and annotations");
        var transfer = new DataStorage(target, () => null, () => { });
        Check(await transfer.InspectAsync(archive, default) is { HasSettings: false, Screenshots: 2 }, "Preview reports both screenshot types separately from settings");
        await Restore(archive, DataImportMode.Replace);
        Check(targetStore.List().SequenceEqual(expected) && File.ReadAllBytes(targetStore.PathFor(plain)).SequenceEqual(original), "Restores plain screenshots and exact history order while also importing clipboard data");
        var reopened = Directory.CreateDirectory(Path.Combine(root, "reopened")).FullName;
        targetStore.PrepareEdit(editable, reopened);
        Check(File.ReadAllBytes(targetStore.PathFor(editable)).SequenceEqual(rendered) &&
            File.ReadAllBytes(Path.Combine(reopened, "edit-source.png")).SequenceEqual(original) &&
            File.ReadAllText(Path.Combine(reopened, "edit-document.json")) == annotated && target.Current.DesktopScreenshots?[editable] == sourceAnchor,
            "Restores current annotation and render, editable original and desktop position");

        var localOnly = targetStore.Import(Path.Combine(session, "pin-source.png"));
        WriteEdit(original, blank);
        Publish(targetStore);
        var localPath = targetStore.PathFor(editable);
        var localAnchor = new LauncherPosition("local-monitor", .6, .8);
        target.Save(target.Current with { DesktopScreenshots = new() { [editable] = localAnchor }, DesktopTraffic = new(localAnchor) });
        await Restore(archive, DataImportMode.Merge);
        Check(targetStore.PathFor(editable) == localPath && target.Current.DesktopScreenshots?[editable] == localAnchor && targetStore.Contains(localOnly),
            "Merge keeps local same-id revision, its desktop position and unrelated screenshot");

        var malformed = Path.Combine(root, "incomplete.zip");
        File.Copy(archive, malformed);
        using (var zip = ZipFile.Open(malformed, ZipArchiveMode.Update)) zip.GetEntry($"screenshots/{editable:N}.pin/source.png")!.Delete();
        try { await Stage(malformed, DataImportMode.Replace); Check(false, "Incomplete editable screenshot must be rejected"); }
        catch (DataStorageException) { Check(targetStore.PathFor(editable) == localPath && File.Exists(localPath), "Missing original rejected before changes"); }

        await Stage(archive, DataImportMode.Replace);
        var before = targetStore.List();
        DataStorage.ImportStep = step => { if (step == "applied-file") throw new IOException("Interrupted screenshot import"); };
        try { target.Load(); }
        finally { DataStorage.ImportStep = null; }
        Check(transfer.LastImport?.Error is not null && targetStore.List().SequenceEqual(before) && targetStore.PathFor(editable) == localPath &&
            File.ReadAllBytes(localPath).SequenceEqual(original) && target.Current.DesktopScreenshots?[editable] == localAnchor,
            "Failed transaction restores revision, artwork, history timestamps and layout");

        await Restore(archive, DataImportMode.Replace);
        var automaticBackup = transfer.LastImport!.Backup;
        Check(targetStore.List().SequenceEqual(expected) && !File.Exists(localPath) && !targetStore.Contains(localOnly) &&
            File.ReadAllBytes(targetStore.PathFor(editable)).SequenceEqual(rendered) && target.Current.DesktopTraffic?.Position == localAnchor,
            "Replace removes obsolete revisions and extra screenshots without changing the traffic card");
        await Restore(automaticBackup, DataImportMode.Replace);
        Check(targetStore.Contains(localOnly) && File.ReadAllBytes(targetStore.PathFor(editable)).SequenceEqual(original) &&
            target.Current.DesktopScreenshots?[editable] == localAnchor, "Automatic backup recovers the previous screenshot state");

        void WriteEdit(byte[] png, string document)
        {
            File.WriteAllBytes(Path.Combine(session, "pin.png"), png);
            File.WriteAllText(Path.Combine(session, "pin-document.json"), document);
        }
        void Publish(ScreenshotStore screenshots)
        {
            var revision = screenshots.StageEdit(editable, session, (1, 1));
            screenshots.PublishEdit(revision);
            screenshots.FinishEdit(revision, accepted: true);
        }
        Task Stage(string path, DataImportMode mode) => transfer.StageImportAsync(path, new(false, false, true, mode, Screenshots: true), null, default);
        async Task Restore(string path, DataImportMode mode)
        {
            await Stage(path, mode);
            target.Load();
            if (transfer.LastImport is not { Error: null }) throw new InvalidOperationException(transfer.LastImport?.Error);
        }
        static ScreenshotStore OpenScreenshots(SettingsStore settings) => new(Path.Combine(settings.DirectoryPath, "screenshots"), settings.StorageSync);
    }
}
