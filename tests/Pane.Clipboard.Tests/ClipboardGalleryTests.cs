using Pane.Contracts;
using Pane.Services.Clipboard;
using Pane.Settings;

static partial class Program
{
    static async Task ClipboardGalleryTests()
    {
        var root = TestResults.NewDirectory("clipboard-gallery-");
        var settings = new SettingsStore(Path.Combine(root, "source"), Defaults());
        var largeFiles = Directory.CreateDirectory(Path.Combine(root, "large-files")).FullName;
        settings.Save(Defaults() with { Clipboard = Defaults().Clipboard with { RecordImages = false }, Storage = new(largeFiles) });
        var image = Path.Combine(root, "photo.png");
        var invalid = Path.Combine(root, "invalid.png");
        var archive = Path.Combine(root, "backup.zip");
        byte[] original = Png(800, 600, 12);
        File.WriteAllBytes(image, original);
        File.WriteAllText(invalid, "not an image");
        var writer = new FakeWriter();

        ClipboardHistory Open(SettingsStore store) => new(store, new FakeSource(), writer, new WindowsClipboardImages());
        using (var history = Open(settings))
        {
            var result = await history.ImportGalleryAsync([invalid, image, image], "Gallery", default);
            Check(result == new ClipGalleryImportResult(1, 1, 1), "gallery imports valid explicit files only and skips duplicates");
            var entry = history.Snapshot(includeArchived: true).Single();
            Check(history.Snapshot().Count == 0 && entry.Archived && entry.ImageWidth == 800 && entry.ImageHeight == 600 &&
                File.Exists(entry.ContentPath) && File.Exists(entry.Thumbnail?.Path), "native decode produces a collection-only original and thumbnail");
            Check(File.ReadAllBytes(image).SequenceEqual(original) && !Path.Equals(image, entry.ContentPath), "does not modify or keep a reference to the selected file");
            var export = Path.Combine(root, "exported.png");
            await history.ExportFileAsync(entry.Id, export, default);
            var exported = await File.ReadAllBytesAsync(export);
            Check(exported.SequenceEqual(File.ReadAllBytes(entry.ContentPath!)) &&
                !exported.SequenceEqual(File.ReadAllBytes(entry.Thumbnail!.Path!)) &&
                writer.Entry is null && history.Snapshot(includeArchived: true).Single() == entry,
                "gallery export copies the full PNG, not the thumbnail, and does not update the clipboard or history");
            var internalDestination = Path.Combine(largeFiles, "blocked.png");
            try { await history.ExportFileAsync(entry.Id, internalDestination, default); }
            catch (InvalidDataException) { }
            Check(!File.Exists(internalDestination) && !Directory.EnumerateFiles(largeFiles, ".pane-clipboard-*.tmp").Any(),
                "gallery export cannot write inside relocated clipboard image storage");
            history.ClearUnpinned();
            await new DataStorage(settings, () => history, () => { }).ExportAsync(archive, new(false, false, ClipboardExport.WithImages), null, default);
        }

        using (var reopened = Open(settings))
        {
            await reopened.Ready;
            Check(reopened.Snapshot().Count == 0 && reopened.Snapshot(includeArchived: true).Single().Collections!.SequenceEqual(["Gallery"]),
                "clearing ordinary history and restarting preserves the gallery");
        }

        var target = new SettingsStore(Path.Combine(root, "target"), Defaults());
        await new DataStorage(target, () => null, () => { }).StageImportAsync(archive, new(false, false, true, DataImportMode.Merge), null, default);
        StorageRecovery.RecoverAll(target.DirectoryPath);
        using (var restored = Open(target))
        {
            await restored.Ready;
            var restoredId = restored.Snapshot(includeArchived: true).Single().Id;
            Check(await restored.ImportGalleryAsync([image], "Gallery", default) == new ClipGalleryImportResult(0, 1, 0),
                "reimporting the selected file after backup restore does not duplicate its normalized PNG");
            Check(await restored.ImportGalleryAsync([image], "Work", default) == new ClipGalleryImportResult(1, 0, 0),
                "importing into another group adds one membership");
            var shared = restored.Snapshot(includeArchived: true).Single();
            Check(shared.Id == restoredId && shared.Collections!.SequenceEqual(["Gallery", "Work"]) && restored.Snapshot().Count == 0,
                "groups share the restored original without changing history visibility");
        }
        foreach (var directory in new[] { largeFiles, Path.Combine(target.DirectoryPath, "clipboard") })
            Check(Directory.EnumerateFiles(directory, "*.png").Count() == 2,
                "gallery folder contains just one original and its thumbnail after persistence");
        Console.WriteLine($"Clipboard gallery: {checks} checks passed; systemClipboard=0.");
    }
}
