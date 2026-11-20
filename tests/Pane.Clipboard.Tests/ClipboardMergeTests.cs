using Pane.Contracts;
using Pane.Localization;
using Pane.Services.Clipboard;
using Pane.Settings;
using System.Text;

static partial class Program
{
    static async Task ClipboardMergeTests()
    {
        var root = TestResults.NewDirectory("clipboard-merge-");
        Directory.CreateDirectory(root);
        var settings = new SettingsStore(root, Defaults());
        settings.Save(settings.Current with { Clipboard = settings.Current.Clipboard with { RecordFiles = true } });
        var source = new FakeSource();
        var writer = new FakeWriter();
        var images = new WindowsClipboardImages();
        var folder = Directory.CreateDirectory(Path.Combine(root, "folder")).FullName;
        var file = Path.Combine(root, "document.txt");
        File.WriteAllText(file, "keep this file");
        string textId, filesId;

        using (var history = new ClipboardHistory(settings, source, writer, images))
        {
            await history.Ready;
            string Capture(ClipboardContent content)
            {
                var before = history.Snapshot().Select(clip => clip.Id).ToHashSet();
                source.Emit(content);
                return history.Snapshot().Single(clip => !before.Contains(clip.Id)).Id;
            }
            var link = Capture(new(Text: "https://example.test"));
            var text = Capture(new(Text: "  Original spacing  "));
            history.SetPinned(link, true);
            var beforeText = history.Snapshot().ToArray();
            Check(await history.MergeAsync([link, text], default) == ClipMergeResult.Saved, "merge link and text");
            var joined = history.Snapshot().Single(clip => beforeText.All(original => original.Id != clip.Id));
            textId = joined.Id;
            Check(joined.Text == "https://example.test" + Environment.NewLine + "  Original spacing  " && !joined.Pinned &&
                beforeText.All(original => history.Snapshot().Contains(original)), "selection order and whitespace; no change to source clips");

            var first = Capture(new(Files: [file]));
            var second = Capture(new(Files: [folder, file.ToUpperInvariant()]));
            Check(await history.MergeAsync([second, first], default) == ClipMergeResult.Saved, "merge Windows file references");
            var joinedFiles = history.Snapshot().Single(clip => clip.Files?.Count == 2 && clip.Id != second);
            filesId = joinedFiles.Id;
            Check(joinedFiles.Files!.SequenceEqual([folder, file.ToUpperInvariant()]) && File.ReadAllText(file) == "keep this file" &&
                writer.Entry is null, "keep first reference and file contents without writing system clipboard");

            var missing = Capture(new(Files: [Path.Combine(root, "missing.txt")]));
            var huge = Capture(new(Text: new string('X', 1_000_000)));
            history.Delete(text);
            var count = history.Snapshot().Count;
            Check(await history.MergeAsync([first, missing], default) == ClipMergeResult.FileUnavailable &&
                await history.MergeAsync([link, first], default) == ClipMergeResult.InvalidSelection &&
                await history.MergeAsync([link, link], default) == ClipMergeResult.InvalidSelection &&
                await history.MergeAsync([link, text], default) == ClipMergeResult.InvalidSelection &&
                await history.MergeAsync([link, huge], default) == ClipMergeResult.TooLarge &&
                history.Snapshot().Count == count, "invalid merges leave history unchanged");
        }

        using var reopened = new ClipboardHistory(settings, new FakeSource(), writer, images);
        await reopened.Ready;
        await reopened.PasteAsync(textId, false, 0, default);
        Check(writer.Entry?.Text == "https://example.test" + Environment.NewLine + "  Original spacing  ", "merged text reloads and can be pasted");
        await reopened.PasteAsync(filesId, false, 0, default);
        Check(writer.Entry?.Files?.SequenceEqual([folder, file.ToUpperInvariant()]) == true, "merged file references reload and can be pasted");
        await ImageMergeTests(settings, root, writer, images);
        Console.WriteLine($"Clipboard merge: {checks} checks passed.");
    }

    static async Task ImageMergeTests(SettingsStore sourceSettings, string root, FakeWriter writer, IClipboardImages images)
    {
        var settings = new SettingsStore(Path.Combine(root, "image-merge"), sourceSettings.Current);
        settings.Save(sourceSettings.Current);
        var imagePath = Path.Combine(root, "sample.png");
        var largePath = Path.Combine(root, "oversize.png");
        var png = Png(32, 16, 64);
        File.WriteAllBytes(imagePath, png);
        using (var sparse = File.Create(largePath)) sparse.SetLength(7 * 1024 * 1024);
        ClipEntry Image(string id, string path) => new(id, ClipKind.Image, null, null, null, 32, 16, "fixture", null, DateTimeOffset.UtcNow, false, path);
        const string body = "中文 <script>&\r\n第二行";
        var text = new ClipEntry("text", ClipKind.Text, body, null, null, null, null, "fixture", null, DateTimeOffset.UtcNow, false);
        var storage = new ClipboardStorage();
        storage.Save(Path.Combine(settings.DirectoryPath, "clipboard", "history.json"),
            [new(text, "text"), new(Image("image", imagePath), "image"), new(Image("large", largePath), "large"),
            new(Image("missing", Path.Combine(root, "missing.png")), "missing")], _ => { });
        string mergedId;
        var archive = Path.Combine(root, "image-merge.zip");
        using (var history = new ClipboardHistory(settings, new FakeSource(), writer, images))
        {
            await history.Ready;
            Check(await history.MergeAsync(["text", "large"], default) == ClipMergeResult.TooLarge &&
                await history.MergeAsync(["text", "missing"], default) == ClipMergeResult.ImageUnavailable && history.Snapshot().Count == 4,
                "invalid image inputs create no partial result");
            Check(await history.MergeAsync(["text", "image"], default) == ClipMergeResult.Saved, "text and image save one composed history entry");
            var added = history.Snapshot().Single(clip => clip.RichText?.ImageMerge == true);
            mergedId = added.Id;
            var bytes = added.RichText!.Html!;
            var header = Encoding.ASCII.GetString(bytes.AsSpan(0, Array.IndexOf(bytes, (byte)'<')));
            int Offset(string field) => int.Parse(header.AsSpan(header.IndexOf(field + ":", StringComparison.Ordinal) + field.Length + 1, 10));
            var fragment = Encoding.UTF8.GetString(bytes.AsSpan(Offset("StartFragment"), Offset("EndFragment") - Offset("StartFragment")));
            Check(added.Text == body + Environment.NewLine + $"[{Strings.Common_Image}]" && Offset("EndHTML") == bytes.Length &&
                fragment == $"<p>中文 &lt;script&gt;&amp;<br>第二行</p><p><img src=\"data:image/png;base64,{Convert.ToBase64String(png)}\" alt=\"\"></p>",
                "multibyte byte offsets, escaping, plain fallback and embedded original bytes");
            Check(await history.MergeAsync([mergedId, "text"], default) == ClipMergeResult.InvalidSelection,
                "do not flatten an existing composition into picture placeholders");
            history.Delete("image");
            history.Delete("large");
            history.Delete("missing");
            File.Delete(imagePath);
            await new DataStorage(settings, () => history, () => { }).ExportAsync(archive, new(false, false, ClipboardExport.WithImages), null, default);
        }
        using (var reload = new ClipboardHistory(settings, new FakeSource(), writer, images))
        {
            await reload.Ready;
            var merged = reload.Snapshot().Single(clip => clip.Id == mergedId);
            var original = reload.Snapshot().Single(clip => clip.Id == "text");
            Check(merged.HasImageContent && !original.HasImageContent,
                "a saved composition still matches the image filter after deleting its source image and reopening history");
            await reload.PasteAsync(mergedId, false, 0, default);
            Check(writer.Entry?.RichText is { ImageMerge: true, Html: { } html } && Encoding.UTF8.GetString(html).Contains(Convert.ToBase64String(png), StringComparison.Ordinal),
                "merged entry reaches writer after independent reload without source image");
        }
        var restored = new SettingsStore(Path.Combine(root, "restored-image-merge"), settings.Current);
        restored.Save(settings.Current);
        await new DataStorage(restored, () => null, () => { }).StageImportAsync(archive, new(false, false, true, DataImportMode.Replace), null, default);
        StorageRecovery.RecoverAll(restored.DirectoryPath);
        var restoredEntry = storage.Load(Path.Combine(restored.DirectoryPath, "clipboard", "history.json"))
            .Single(clip => clip.Entry.RichText?.ImageMerge == true).Entry;
        Check(restoredEntry.RichText!.Html!.SequenceEqual(writer.Entry!.RichText!.Html!), "portable backup retains the composition byte for byte");
    }
}
