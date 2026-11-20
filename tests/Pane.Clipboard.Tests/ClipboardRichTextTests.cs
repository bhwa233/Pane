using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Clipboard;
using Pane.Settings;

static partial class Program
{
    static async Task ClipboardRichTextTests()
    {
        var root = TestResults.NewDirectory("clipboard-rich-text-");
        var settings = new SettingsStore(Path.Combine(root, "source"), Defaults());
        settings.Save(Defaults());
        var source = new FakeSource();
        var writer = new FakeWriter();
        var images = new WindowsClipboardImages();
        var html = Encoding.UTF8.GetBytes("Version:1.0\r\n<b>Sample</b>");
        var rtf = "{\\rtf1\\b Sample}"u8.ToArray();
        var italic = "{\\rtf1\\i Sample}"u8.ToArray();
        var clipboard = new ClipboardContent(Text: "Sample", RichText: new(html, rtf));
        var plainZip = Path.Combine(root, "plain.zip");
        var completeZip = Path.Combine(root, "complete.zip");
        string richId;

        using (var history = new ClipboardHistory(settings, source, writer, images))
        {
            await history.Ready;
            source.Emit(clipboard with { Cloud = 0 });
            Check(history.Snapshot().Count == 0, "rich capture honors source privacy markers");
            source.Emit(clipboard);
            richId = history.Snapshot().Single().Id;
            source.Emit(clipboard with { RichText = new(html.ToArray(), rtf.ToArray()) });
            Check(history.Snapshot().Single().Id == richId, "equivalent raw formats dedupe");
            source.Emit(clipboard with { RichText = new(html, italic) });
            Check(history.Snapshot().Count == 2, "same unicode text with different RTF remains distinct");
            source.Emit(clipboard with { Text = "Bounded", RichText = new(new byte[ClipboardTextFormats.FormatByteLimit + 1], rtf) });
            Check(history.Snapshot().Single(clip => clip.Text == "Bounded").RichText is { Html: null, Rtf: not null }, "oversize HTML keeps the other representations");
            var storage = new DataStorage(settings, () => history, () => { });
            await storage.ExportAsync(plainZip, new(false, false, ClipboardExport.TextOnly), null, default);
            await storage.ExportAsync(completeZip, new(false, false, ClipboardExport.WithImages), null, default);
        }

        using (var reopened = new ClipboardHistory(settings, new FakeSource(), writer, images))
        {
            await reopened.Ready;
            await reopened.PasteAsync(richId, false, 0, default);
            Check(writer.Entry?.Text == "Sample" && writer.Entry.RichText is { Html: not null, Rtf: not null } restored &&
                restored.Html.SequenceEqual(html) && restored.Rtf.SequenceEqual(rtf), "exact formats survive reload and reach the history writer");
        }

        async Task<StoredClip[]> Restore(string zip, string name)
        {
            var target = new SettingsStore(Path.Combine(root, name), Defaults());
            target.Save(Defaults());
            await new DataStorage(target, () => null, () => { }).StageImportAsync(zip, new(false, false, true, DataImportMode.Merge), null, default);
            StorageRecovery.RecoverAll(target.DirectoryPath);
            return new ClipboardStorage().Load(Path.Combine(target.DirectoryPath, "clipboard", "history.json"));
        }
        var plain = await Restore(plainZip, "plain");
        Check(plain.Length == 2 && plain.All(clip => clip.Entry.RichText is null), "plain export has no embedded raw formats and plain merge de-duplicates");
        var complete = await Restore(completeZip, "complete");
        Check(complete.Length == 3 && complete.Count(clip => clip.Entry.Text == "Sample" && clip.Entry.RichText?.Html?.SequenceEqual(html) == true) == 2 &&
            complete.Any(clip => clip.Entry.RichText?.Rtf?.SequenceEqual(italic) == true), "complete archive preserves distinct formatting after import");

        var burst = new FakeSource();
        using var bounded = new ClipboardHistory(settings, burst, writer, images);
        await bounded.Ready;
        bounded.SetPinned(richId, true);
        var heavyHtml = Enumerable.Repeat((byte)'x', ClipboardTextFormats.FormatByteLimit).ToArray();
        var burstCount = ClipboardTextFormats.HistoryByteLimit / heavyHtml.Length + 1;
        foreach (var index in Enumerable.Range(0, burstCount)) burst.Emit(new(Text: $"Burst {index}", RichText: new(heavyHtml, null)));
        var snapshot = bounded.Snapshot();
        Check(snapshot.Count(clip => clip.Text!.StartsWith("Burst ", StringComparison.Ordinal)) == burstCount &&
            snapshot.Single(clip => clip.Id == richId).RichText is not null &&
            snapshot.Single(clip => clip.Text == "Burst 0").RichText is null &&
            snapshot.Single(clip => clip.Text == $"Burst {burstCount - 1}").RichText is not null, "total limit drops older raw formats before pinned content and keeps plain history");
        await ClipboardTextCopyChecks(root, clipboard, writer, images);
        Console.WriteLine($"Clipboard rich text: {checks} checks passed.");
    }

    static async Task ClipboardTextCopyChecks(string root, ClipboardContent clipboard, FakeWriter writer, IClipboardImages images)
    {
        var options = Defaults();
        var settings = new SettingsStore(Path.Combine(root, "text-copy"), options);
        settings.Save(options with { Clipboard = options.Clipboard with { Limit = 200 } });
        var source = new FakeSource();
        const string edited = "https://example.test/edited";
        var previousWrite = writer.Entry;
        string originalId, copyId;
        using (var history = new ClipboardHistory(settings, source, writer, images))
        {
            await history.Ready;
            const string exportedText = "\u526a\u8d34\u677f\r\nsecond line \U0001f600";
            source.Emit(clipboard with { Text = exportedText });
            var original = history.Snapshot().Single();
            originalId = original.Id;
            var destination = Path.Combine(root, "clip.txt");
            await File.WriteAllTextAsync(destination, "previous contents");
            await history.ExportFileAsync(originalId, destination, default);
            var expected = Encoding.UTF8.GetBytes(exportedText);
            Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(expected) && history.Snapshot().Single() == original &&
                ReferenceEquals(writer.Entry, previousWrite), "single-item export replaces the destination with exact UTF-8 without changing history or clipboard");
            try { await history.ExportFileAsync(originalId, destination, new CancellationToken(true)); }
            catch (OperationCanceledException) { }
            Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(expected) && !Directory.EnumerateFiles(root, ".pane-clipboard-*.tmp").Any(),
                "canceling export leaves the destination and its directory unchanged");
            var internalDestination = Path.Combine(settings.DirectoryPath, "blocked.txt");
            try { await history.ExportFileAsync(originalId, internalDestination, default); }
            catch (InvalidDataException) { }
            Check(!File.Exists(internalDestination), "single-item export cannot write inside the Pane data directory");
            Check(history.SaveTextCopy(original.Id, original.Text!) == ClipTextCopyResult.InvalidText &&
                history.SaveTextCopy(original.Id, "invalid\0text") == ClipTextCopyResult.InvalidText, "unchanged or truncated clipboard text is not saved as a copy");
            for (var index = 1; index < settings.Current.Clipboard.Limit; index++) source.Emit(new(Text: $"filler {index}"));
            var full = history.Snapshot();
            Check(history.SaveTextCopy(original.Id, edited) == ClipTextCopyResult.HistoryFull && history.Snapshot().SequenceEqual(full),
                "saving at capacity must not evict the original or modify any other row on failure");
            Check(history.SetTextDraft(originalId, edited), "an unpinned text item accepts a local draft");
            source.Emit(new(Text: "one more item"));
            Check(history.Snapshot().Any(clip => clip.Id == originalId) &&
                history.SaveTextCopy(originalId, edited) == ClipTextCopyResult.HistoryFull && history.GetTextDraft(originalId) == edited,
                "automatic trimming and a failed save must leave the original and its draft available");
            var archive = Path.Combine(root, "no-drafts.zip");
            await new DataStorage(settings, () => history, () => { }).ExportAsync(archive,
                new(false, false, ClipboardExport.WithImages), null, default);
            using var zip = ZipFile.OpenRead(archive);
            using var indexFile = zip.GetEntry("clipboard/history.json")!.Open();
            var portable = JsonSerializer.Deserialize(indexFile, ClipboardJson.Default.StoredClipArray)!;
            Check(portable.Single(clip => clip.Entry.Id == originalId) is { TextDraft: null, Entry.Text: exportedText } &&
                portable.All(clip => clip.Entry.Text != edited), "the portable archive only includes committed clipboard content");
        }
        using (var history = new ClipboardHistory(settings, new FakeSource(), writer, images))
        {
            await history.Ready;
            var original = history.Snapshot().Single(clip => clip.Id == originalId);
            Check(history.GetTextDraft(originalId) == edited && ReferenceEquals(writer.Entry, previousWrite) && original.Text != edited,
                "after restart the draft is available only to the editor, not the original or clipboard");
            var discard = history.Snapshot().First(clip => clip.Id != originalId);
            Check(history.SetTextDraft(discard.Id, "unsaved") && history.SetTextDraft(discard.Id, null) && history.GetTextDraft(discard.Id) is null,
                "discard removes just the draft");
            history.SetPinned(original.Id, true);
            var untouched = history.Snapshot().Single(clip => clip.Id == originalId);
            Check(history.SaveTextCopy(originalId, edited) == ClipTextCopyResult.Saved && history.GetTextDraft(originalId) is null &&
                history.Snapshot().Single(clip => clip.Id == originalId) == untouched,
                "an edited copy leaves the original pinned rich text unchanged");
            var copy = history.Snapshot().Single(clip => clip.Text == edited);
            Check(copy.Id != originalId && copy.Kind == ClipKind.Link && !copy.Pinned && copy.RichText is null,
                "a saved copy gets a fresh ID and classification without the source formatting");
            copyId = copy.Id;
        }
        using var reopened = new ClipboardHistory(settings, new FakeSource(), writer, images);
        await reopened.Ready;
        Check(reopened.Snapshot().Single(clip => clip.Id == originalId) is { Pinned: true, RichText.Html: { } originalHtml } &&
            originalHtml.SequenceEqual(clipboard.RichText!.Html!) && reopened.Snapshot().Single(clip => clip.Id == copyId).Text == edited &&
            reopened.GetTextDraft(originalId) is null,
            "the original rich text and the edited plain copy both survive a restart");
    }
}
