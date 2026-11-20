using Pane.Services.Notes;
using Pane.Settings;

static class NoteHistoryTests
{
    public static int Run()
    {
        var directory = Directory.CreateTempSubdirectory("pane-note-history-");
        try { return Run(directory.FullName); }
        finally { directory.Delete(recursive: true); }
    }

    static int Run(string directory)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }

        var store = new NoteStore(directory);
        store.Load();
        store.Select(null);
        store.Edit("Old title", "Old text");
        store.SaveDraft();
        var original = store.Draft!;
        Check(store.Versions(original.Id).Count == 0, "a first save has no previous version");

        store.Edit("New title", "New text", " Work ");
        var history = Path.Combine(directory, ".history");
        File.WriteAllText(history, "blocking file");
        bool refused = false;
        try { store.SaveDraft(); }
        catch (IOException) { refused = true; }
        var reopened = new NoteStore(directory);
        reopened.Load();
        Check(refused && reopened.Find(original.Id) == original && store.HasChanges && store.Draft is { Body: "New text", Category: "Work" },
            "archive failure leaves the current file and unsaved draft intact");
        File.Delete(history);

        var blockingDirectory = Path.Combine(directory, $"{original.Id:N}.json.tmp");
        Directory.CreateDirectory(blockingDirectory);
        refused = false;
        try { store.SaveDraft(); }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { refused = true; }
        Check(refused && store.Versions(original.Id).Count == 0 && store.HasChanges,
            "a failed current-file commit does not publish a duplicate version");
        Directory.Delete(blockingDirectory);
        store.SaveDraft();
        var replaced = store.Draft!;

        reopened = new NoteStore(directory);
        reopened.Load();
        reopened.Select(original.Id);
        Check(reopened.Draft?.Category == "Work", "the assigned category survives restart");
        var version = reopened.Versions(original.Id).Single();
        Check(reopened.ReadVersion(original.Id, version) == original, "the original title and body survive restart");
        reopened.Edit("Unsaved title", "Unsaved text");
        Check(!reopened.RestoreVersion(original.Id, version) && reopened.HasChanges,
            "restore never overwrites an unsaved draft");
        reopened.Edit(replaced.Title, replaced.Body);
        Check(reopened.RestoreVersion(original.Id, version) && reopened.Draft is { Title: "Old title", Body: "Old text", Category: "" } restored && restored.Id == original.Id,
            "restore updates the same note");
        Check(reopened.ReadVersion(original.Id, reopened.Versions(original.Id).First()) == replaced,
            "restore remains reversible");

        for (int index = 0; index < 32; index++)
        {
            reopened.Edit("Title", $"Body {index}");
            reopened.SaveDraft();
        }
        var retained = reopened.Versions(original.Id);
        Check(retained.Count == 30 && retained.All(date => date > version) && Directory.GetFiles(history, "*.json").Length == 30,
            "old versions are pruned on disk and the newest 30 remain");

        reopened.Select(null);
        reopened.Edit("Other note", "Its own body");
        reopened.SaveDraft();
        var other = reopened.Draft!;
        reopened.Edit(other.Title, other.Body, "Personal");
        Check(reopened.SaveDraft() && reopened.Draft is { Category: "Personal" } assigned && assigned.Id == other.Id,
            "changing only the category persists without replacing the note");
        reopened.Select(original.Id);
        Check(!reopened.DeleteRecycled(original.Id), "Permanent deletion cannot remove an active note");
        reopened.Edit("Latest title", "Latest draft body");
        reopened.RecycleDraft();
        reopened = new NoteStore(directory);
        reopened.Load();
        Check(reopened.Documents.Single().Id == other.Id && reopened.Find(original.Id) == null &&
            reopened.RecycledDocuments.Single() is { Body: "Latest draft body" } recycled && recycled.Id == original.Id &&
            reopened.Versions(original.Id).Count == 30, "The recycled note retains its latest content and history but stays out of the active list after restart");
        reopened.Select(other.Id);
        Check(reopened.RestoreRecycled(original.Id) && reopened.Find(original.Id)?.Title == "Latest title" &&
            !reopened.RecycledDocuments.Any() && reopened.Draft?.Id == other.Id,
            "Restoring a note retains its ID and does not replace another editor draft");
        reopened.Select(original.Id);
        reopened.RecycleDraft();
        Check(reopened.DeleteRecycled(original.Id) && !File.Exists(reopened.PathFor(original.Id)) && !reopened.RecycledDocuments.Any() &&
            !Directory.EnumerateFiles(history, $"{original.Id:N}-*").Any() && reopened.Versions(other.Id).Count == 1,
            "Permanently deleting a recycled note removes only its own content and history");

        var files = Directory.CreateDirectory(Path.Combine(directory, "files")).FullName;
        var managed = new NoteStore(Path.Combine(directory, "other-notes"));
        managed.Load();
        managed.Select(null);
        var markdown = Path.Combine(files, "会议.md");
        const string source = "# 议程\r\n\r\n- 测试\r\n";
        File.WriteAllText(markdown, source, System.Text.Encoding.UTF8);
        var before = File.ReadAllBytes(markdown);
        var imported = NoteStore.ReadFile(markdown);
        managed.ImportFile(imported.Title, imported.Body);
        var note = managed.Draft!;
        Check(note.Title == "会议" && note.Body == "# 议程\n\n- 测试\n" && File.ReadAllBytes(markdown).SequenceEqual(before),
            "Import strips the UTF-8 BOM, preserves Markdown content, and does not change the source");

        managed.Edit(note.Title, "Unwritten draft");
        refused = false;
        try { managed.ImportFile("Other", "Other content"); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused && managed.Draft is { Body: "Unwritten draft" } && managed.Draft.Id == note.Id && managed.Find(note.Id) == note,
            "Import cannot overwrite an unsaved draft or its saved copy");

        managed.SaveNew("Translation", "译文\r\n第二行");
        var afterSaveNew = new NoteStore(Path.Combine(directory, "other-notes"));
        afterSaveNew.Load();
        Check(afterSaveNew.Documents.Any(item => item.Id != note.Id && item.Title == "Translation" && item.Body == "译文\n第二行") &&
            managed.HasChanges && managed.Draft is { Body: "Unwritten draft" } && managed.Draft.Id == note.Id && afterSaveNew.Find(note.Id) == note,
            "A saved tool result survives restart without committing or replacing the active editor draft");

        File.WriteAllBytes(Path.Combine(files, "broken.md"), [0xC3, 0x28]);
        refused = false;
        try { NoteStore.ReadFile(Path.Combine(files, "broken.md")); }
        catch (System.Text.DecoderFallbackException) { refused = true; }
        Check(refused, "Malformed UTF-8 is rejected instead of importing replacement characters");

        var exported = Path.Combine(files, "export.txt");
        File.WriteAllText(exported, "Previous destination contents");
        managed.ExportFile(managed.Draft!, exported, default);
        Check(File.ReadAllText(exported) == "Unwritten draft" && managed.HasChanges && managed.Find(note.Id) == note,
            "Export replaces the selected destination without committing the draft or changing its version history");

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC");
        managed.AddImage(png);
        var image = managed.Draft!.Images!.Single();
        managed.SaveDraft();
        var withImage = managed.Draft!.Updated;
        managed.RemoveImage(image);
        managed.SaveDraft();
        Check(managed.Draft!.Images == null && managed.FindImage(note.Id, image) != null && managed.RestoreVersion(note.Id, withImage) &&
            managed.Draft!.Images!.Single() == image, "A version differing only by an image restores it without losing the managed PNG");
        managed.RemoveImage(image);
        managed.SaveDraft();
        for (int index = 0; index < 31; index++)
        {
            managed.Edit(note.Title, $"Image-free draft {index}");
            managed.SaveDraft();
        }
        Check(!File.Exists(managed.ImagePath(note.Id, image)), "An image is pruned after no retained version refers to it");

        var draftBeforeImageFailure = managed.Draft;
        var imageFolder = Path.GetDirectoryName(managed.ImagePath(note.Id, image))!;
        Directory.Delete(imageFolder);
        File.WriteAllText(imageFolder, "Cannot create image directory");
        refused = false;
        try { managed.AddImage(png); }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { refused = true; }
        Check(refused && managed.Draft == draftBeforeImageFailure && !managed.HasChanges,
            "An image write failure leaves the draft and saved note intact");

        var defaults = new PaneSettings(1, new(false, true, ThemeMode.Dark, "#FF5874C9", 20, 1,
            new(.2, .2, 1, 1, 1, 0), new(.2, .2, 1, 1, 1, 0)),
            new(new(KeyModifiers.Alt, 32)), new(new(KeyModifiers.Ctrl, 86)));
        var settingsDirectory = Path.Combine(directory, "reminder-settings");
        var settingsStore = new SettingsStore(settingsDirectory, defaults);
        var due = DateTimeOffset.UnixEpoch.AddDays(1);
        settingsStore.Update(settings => NoteReminderSchedule.Set(settings, note.Id, null, due.AddHours(1)));
        settingsStore.Update(settings => NoteReminderSchedule.Set(settings, note.Id, "Send report", due));
        settingsStore.Update(settings => NoteReminderSchedule.Set(settings, note.Id, "File receipt", due.AddHours(2)));
        settingsStore.Update(settings => NoteReminderSchedule.Set(settings, other.Id, "Send report", due.AddHours(3)));
        settingsStore = new SettingsStore(settingsDirectory, defaults);
        settingsStore.Load();
        Check(NoteReminderSchedule.Next(settingsStore.Current) == new NoteReminder(note.Id, "Send report", due),
            "The earliest task or whole-note reminder is selected after reopening settings");
        settingsStore.Update(settings => NoteReminderSchedule.Set(settings, note.Id, "Send report", due.AddHours(4)));
        Check(NoteReminderSchedule.Next(settingsStore.Current) == new NoteReminder(note.Id, null, due.AddHours(1)),
            "Rescheduling a task replaces its earlier slot without changing the whole-note reminder");
        settingsStore.Update(settings => NoteReminderSchedule.Set(settings, note.Id, null, null));
        Check(NoteReminderSchedule.Next(settingsStore.Current) == new NoteReminder(note.Id, "File receipt", due.AddHours(2)),
            "Cancelling a whole-note reminder leaves task reminders scheduled");
        settingsStore.Update(settings => NoteReminderSchedule.RemoveNote(settings, note.Id));
        Check(NoteReminderSchedule.Next(settingsStore.Current) == new NoteReminder(other.Id, "Send report", due.AddHours(3)),
            "Removing a note clears its task reminders without touching a same-named task in another note");
        settingsStore.Update(settings => NoteReminderSchedule.Set(settings, other.Id, "Send report", null));
        Check(NoteReminderSchedule.Next(settingsStore.Current) == null,
            "Removing the delivered task leaves no pending schedule");
        return checks;
    }
}
