using Pane.Contracts;
using Pane.Services.Clipboard;
using Pane.Settings;

static partial class Program
{
    static async Task ClipboardCollectionsTests()
    {
        var root = TestResults.NewDirectory("clipboard-collections-");
        Check(ClipboardCollections.TryParse(" Work， Notes,work ", out var groups) && groups!.SequenceEqual(["Work", "Notes"]) &&
            !ClipboardCollections.TryParse("line\nbreak", out _), "canonical membership names keep first occurrence and reject controls");
        SettingsStore Store(string name)
        {
            var target = new SettingsStore(Path.Combine(root, name), Defaults());
            target.Save(Defaults() with { Clipboard = Defaults().Clipboard with { ImageLimitMegabytes = 1 } });
            return target;
        }
        var settings = Store("source");
        var storage = new ClipboardStorage();
        var imageStore = new WindowsClipboardImages();
        var writer = new FakeWriter();
        var imageDirectory = Directory.CreateDirectory(Path.Combine(settings.DirectoryPath, "clipboard")).FullName;
        var imagePath = Path.Combine(imageDirectory, "collection.png");
        var uncollectedPath = Path.Combine(imageDirectory, "ordinary.png");
        File.WriteAllBytes(imagePath, Png(40, 30, 10));
        File.WriteAllBytes(uncollectedPath, Png(40, 30, 11));
        using (var file = File.OpenWrite(imagePath)) file.SetLength(2 * 1024 * 1024);
        ClipEntry Image(string id, string path, IReadOnlyList<string>? names) => new(id, ClipKind.Image, null, null, null, 40, 30,
            "fixture", null, DateTimeOffset.UtcNow, false, ContentPath: path, Collections: names);
        storage.Save(Path.Combine(imageDirectory, "history.json"),
            [new(Image("image", imagePath, groups), "image"), new(Image("ordinary", uncollectedPath, null), "ordinary")], _ => { });

        var source = new FakeSource();
        string textId;
        var zip = Path.Combine(root, "collections.zip");
        using (var history = new ClipboardHistory(settings, source, writer, imageStore))
        {
            await history.Ready;
            Check(history.Snapshot().Count == 2 && File.Exists(imagePath), "collected original is outside automatic history image quota");
            source.Emit(new(Text: "Shared text"));
            textId = history.Snapshot().Single(clip => clip.Text == "Shared text").Id;
            Check(history.SetDetails(textId, null, groups), "a single text record can be collected into two groups");
            Check(history.SetFavorite(textId, true), "a grouped record can also be favorited");
            history.ClearUnpinned();
            Check(history.Snapshot().Count == 0 && history.Snapshot(includeArchived: true).Count == 2 && File.Exists(imagePath),
                "ordinary clear hides both collected items and drops the uncollected item");
            source.Emit(new(Text: "Shared text"));
            Check(history.Snapshot().Single().Id == textId && history.Snapshot(includeArchived: true).Count == 2, "recopy restores same collected item without duplication");
            history.Delete(textId);
            await new DataStorage(settings, () => history, () => { }).ExportAsync(zip, new(false, false, ClipboardExport.WithImages), null, default);
        }
        Check(File.Exists(imagePath) && !File.Exists(uncollectedPath), "retained asset and ordinary asset cleanup differ after durable save");

        using (var reopened = new ClipboardHistory(settings, new FakeSource(), writer, imageStore))
        {
            await reopened.Ready;
            Check(reopened.Snapshot().Count == 0 && reopened.Snapshot(includeArchived: true).Single(clip => clip.Id == textId).Collections!.SequenceEqual(groups!),
                "reopening preserves archived status and memberships");
            Check(reopened.Snapshot(includeArchived: true).Single(clip => clip.Id == textId).Favorite,
                "reopening preserves a favorite after it leaves history");
            await reopened.PasteAsync("image", false, 0, default);
            Check(writer.Entry?.ContentPath == imagePath && reopened.Snapshot().Count == 0, "archived image still reaches paste without leaving collection-only state");
            Check(reopened.SetDetails("image", null, ["Notes"]) && File.Exists(imagePath), "one remaining reference keeps original");
            Check(reopened.SetDetails("image", null, null) && reopened.Snapshot(includeArchived: true).All(clip => clip.Id != "image"), "final reference removes the archived entry");
        }
        Check(!File.Exists(imagePath), "final reference releases image after index save");

        var target = Store("target");
        var localSource = new FakeSource();
        using (var local = new ClipboardHistory(target, localSource, writer, imageStore))
        {
            await local.Ready;
            localSource.Emit(new(Text: "Shared text"));
            local.SetDetails(local.Snapshot().Single().Id, new("Local annotation", []), ["work", "Local"]);
        }
        await new DataStorage(target, () => null, () => { }).StageImportAsync(zip, new(false, false, true, DataImportMode.Merge), null, default);
        StorageRecovery.RecoverAll(target.DirectoryPath);
        using var restored = new ClipboardHistory(target, new FakeSource(), writer, imageStore);
        await restored.Ready;
        var importedImage = restored.Snapshot(includeArchived: true).Single(clip => clip.Kind == ClipKind.Image);
        var mergedText = restored.Snapshot().Single();
        Check(importedImage.Archived && importedImage.Collections!.SequenceEqual(groups!) && File.Exists(importedImage.ContentPath), "import restores collection-only original");
        Check(mergedText.Favorite && mergedText.Collections!.SequenceEqual(["work", "Local", "Notes"]) && mergedText.Annotation!.Note == "Local annotation",
            "merge import unions memberships without duplicating records or replacing local details");

        var cleanupSettings = Store("cleanup");
        cleanupSettings.Save(cleanupSettings.Current with { Clipboard = cleanupSettings.Current.Clipboard with { Dedupe = true, Limit = 200 } });
        var cleanupSource = new FakeSource();
        using var cleanup = new ClipboardHistory(cleanupSettings, cleanupSource, writer, imageStore);
        await cleanup.Ready;
        string Capture(string text)
        {
            cleanupSource.Emit(new(Text: text));
            return cleanup.Snapshot().Single(clip => clip.Text == text).Id;
        }
        var collected = Capture("collected");
        cleanup.SetDetails(collected, null, ["Notes"]);
        var favorite = Capture("favorite without a group");
        cleanup.SetFavorite(favorite, true);
        var archived = Capture("archived");
        cleanup.SetDetails(archived, null, ["Notes"]);
        cleanup.Delete(archived);
        var pinned = Capture("already pinned");
        cleanup.SetPinned(pinned, true);
        var draft = Capture("draft original");
        Check(cleanup.SetTextDraft(draft, "keep this draft"), "cleanup fixture stores a draft");
        for (int index = 0; index < 60; index++) Capture($"ordinary candidate {index}");
        var changedDraft = Capture("start editing after preview");
        var recopied = Capture("copy again");
        var preview = cleanup.PreviewCleanup();
        var shown = preview.Entries.Select(clip => clip.Id).ToHashSet();
        var hidden = cleanup.Snapshot().Where(clip => !shown.Contains(clip.Id)).Select(clip => clip.Id).ToHashSet();
        Check(preview.Remaining > 0 && !shown.Overlaps([collected, favorite, archived, pinned, draft]) && shown.IsSupersetOf([recopied, changedDraft]),
            "the bounded preview includes eligible records and leaves protected records out");
        Check(Capture("copy again") == recopied && cleanup.SetTextDraft(changedDraft, "new draft"), "shown records can be copied again or edited before confirmation");
        var added = Capture("added after preview");
        hidden.UnionWith([recopied, changedDraft, added]);
        Check(cleanup.ConfirmCleanup(preview) == shown.Count - 2 && cleanup.Snapshot().Select(clip => clip.Id).ToHashSet().SetEquals(hidden),
            "confirmation deletes only unchanged preview records and never fills the batch with unshown records");
        Check(cleanup.ConfirmCleanup(preview) == 0 && cleanup.GetTextDraft(draft) == "keep this draft" && cleanup.GetTextDraft(changedDraft) == "new draft" &&
            cleanup.Snapshot(includeArchived: true).Any(clip => clip.Id == archived && clip.Archived), "repeat confirmation preserves existing and new drafts and archived collections");
        cleanup.Delete(favorite);
        Check(cleanup.Snapshot(includeArchived: true).Any(clip => clip.Id == favorite && clip.Archived && clip.Favorite),
            "removing a favorite from history keeps it accessible through favorites");
        Check(cleanup.SetFavorite(favorite, false) && cleanup.Snapshot(includeArchived: true).All(clip => clip.Id != favorite),
            "removing the last favorite reference releases the archived entry");
        Console.WriteLine($"Clipboard collections: {checks} checks passed.");
    }
}
