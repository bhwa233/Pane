using Pane.Services.Screenshots;

static class ScreenshotExportTests
{
    internal static int Run()
    {
        var original = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC");
        var rendered = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY2BgYPgPAAEEAQBwIGULAAAAAElFTkSuQmCC");
        const string blankDocument = """{"format":"spark233-pin","version":1,"width":1,"height":1,"shapes":[]}""";
        const string editedDocument = """{"format":"spark233-pin","version":1,"width":1,"height":1,"shapes":[{"type":"line"}]}""";
        var directory = Path.Combine(TestResults.NewDirectory("screenshot-export-"), new string('a', 128), new string('b', 128));
        var history = Directory.CreateDirectory(Path.Combine(directory, "history")).FullName;
        var session = Directory.CreateDirectory(Path.Combine(directory, "session")).FullName;
        var destination = Path.Combine(directory, "Screenshot.png");
        var store = new ScreenshotStore(history);
        var source = Path.Combine(session, "pin-source.png");
        var image = Path.Combine(session, "pin.png");
        var document = Path.Combine(session, "pin-document.json");
        File.WriteAllBytes(source, original);
        File.WriteAllBytes(image, original);
        File.WriteAllText(document, blankDocument);
        var id = store.ImportEditable(session, (1, 1));
        File.WriteAllBytes(image, rendered);
        File.WriteAllText(document, editedDocument);
        var edit = store.StageEdit(id, session, (1, 1));
        store.PublishEdit(edit);
        store.FinishEdit(edit, true);
        var entries = store.List();

        File.WriteAllBytes(destination, original);
        store.Export(id, destination, default);
        if (!File.ReadAllBytes(destination).SequenceEqual(rendered))
            throw new InvalidOperationException("Export did not replace the selected PNG with the latest rendered revision.");

        try
        {
            store.Export(id, store.PathFor(id), default);
            throw new InvalidOperationException("Export accepted a destination inside screenshot history.");
        }
        catch (InvalidDataException) { }

        File.WriteAllBytes(destination, original);
        try
        {
            store.Export(id, destination, new CancellationToken(true));
            throw new InvalidOperationException("Canceled export was published.");
        }
        catch (OperationCanceledException) { }
        if (!File.ReadAllBytes(destination).SequenceEqual(original) || Directory.EnumerateFiles(directory, "*.tmp").Any())
            throw new InvalidOperationException("Canceled export changed the destination or left a temporary file.");

        store.PrepareEdit(id, session);
        if (!store.List().SequenceEqual(entries) ||
            !File.ReadAllBytes(store.PathFor(id)).SequenceEqual(rendered) ||
            !File.ReadAllBytes(Path.Combine(session, "edit-source.png")).SequenceEqual(original) ||
            File.ReadAllText(Path.Combine(session, "edit-document.json")) != editedDocument)
            throw new InvalidOperationException("Export changed the history, editable source or annotation document.");

        var repeatEditSession = Directory.CreateDirectory(Path.Combine(directory, "repeat-edit")).FullName;
        VerifyRetention(store, history, repeatEditSession, source, id, editedDocument);
        VerifyLockedDelete(store, id, rendered, editedDocument);
        VerifyReadOnlyDelete(store, session, source);
        return 7;
    }

    static void VerifyLockedDelete(ScreenshotStore store, Guid id, byte[] rendered, string document)
    {
        var currentImage = store.PathFor(id);
        var currentDocument = Path.Combine(Path.GetDirectoryName(currentImage)!, "document.json");
        using (var locked = new FileStream(currentDocument, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                store.Delete(id);
                throw new InvalidOperationException("Deleting a locked screenshot must fail.");
            }
            catch (IOException) { }

            if (!store.IsEditable(id) || !store.List().Any(entry => entry.Id == id) ||
                store.PathFor(id) != currentImage || !File.ReadAllBytes(currentImage).SequenceEqual(rendered) ||
                File.ReadAllText(currentDocument) != document)
                throw new InvalidOperationException("A failed delete changed the current screenshot revision.");
        }

        store.Delete(id);
        var record = Path.GetDirectoryName(Path.GetDirectoryName(currentImage))!;
        if (store.Contains(id) || store.IsEditable(id) || Directory.Exists(record) || Directory.Exists(record + ".deleted"))
            throw new InvalidOperationException("Retry did not remove the complete screenshot record.");
    }

    static void VerifyReadOnlyDelete(ScreenshotStore store, string session, string source)
    {
        var id = store.ImportEditable(session, (1, 1));
        var record = Path.GetDirectoryName(store.PathFor(id))!;
        var staged = record + ".deleted";
        var original = Path.Combine(record, "source.png");
        File.SetAttributes(original, FileAttributes.ReadOnly);
        try
        {
            try
            {
                store.Delete(id);
                throw new InvalidOperationException("Deleting a read-only screenshot must fail.");
            }
            catch (InvalidDataException) { }
        }
        finally { if (File.Exists(original)) File.SetAttributes(original, FileAttributes.Normal); }

        var next = store.Import(source);
        if (Directory.Exists(staged) || !store.IsEditable(id) || !store.Contains(id) || !store.Contains(next))
            throw new InvalidOperationException("Failed deletion or subsequent import changed the existing screenshot.");
        store.Delete(id);
    }

    static void VerifyRetention(ScreenshotStore store, string history, string session, string source, Guid pinned, string document)
    {
        var baseTime = DateTime.UnixEpoch.AddYears(45);
        File.SetCreationTimeUtc(Path.Combine(history, pinned.ToString("N") + ".pin", "image.png"), baseTime);
        var unpinned = Enumerable.Range(1, ScreenshotStore.UnpinnedHistoryLimit + 2).Select(index =>
        {
            var id = store.Import(source);
            File.SetCreationTimeUtc(store.PathFor(id), baseTime.AddMinutes(index));
            return id;
        }).ToArray();

        var added = unpinned[^1];
        File.SetCreationTimeUtc(store.PathFor(added), baseTime.AddMinutes(-1));
        if (!store.TrimUnpinnedHistory(added, id => id == pinned) ||
            unpinned.Take(2).Any(store.Contains) || !unpinned.Skip(2).All(store.Contains) || !store.Contains(pinned))
            throw new InvalidOperationException("Retention must keep the pinned record and the new capture, pruning only the oldest excess unpinned records.");

        store.PrepareEdit(pinned, session);
        if (!File.ReadAllBytes(Path.Combine(session, "edit-source.png")).SequenceEqual(File.ReadAllBytes(source)) ||
            File.ReadAllText(Path.Combine(session, "edit-document.json")) != document)
            throw new InvalidOperationException("Retention modified the pinned screenshot's original or annotations.");
    }
}
