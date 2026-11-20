using System.Text.Json;
using Pane.Services.Screenshots;

static class OcrHistoryTests
{
    internal static int Run()
    {
        var root = TestResults.NewDirectory("pane-ocr-history-");
        var history = Path.Combine(root, "ocr-history");
        var screenshots = Path.Combine(root, "screenshots");
        var fixture = Path.Combine(root, "fixture.png");
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC");
        var payload = """{"width":1,"height":1,"text":"Pane OCR","lines":[{"text":"Pane OCR","box":[[0,0],[1,0],[1,1],[0,1]]}]}"""u8.ToArray();
        using var result = JsonDocument.Parse(payload);
        var recognition = NativeScreenshotOcr.ReadRecognition(result.RootElement, (1, 1));
        var screenshotStore = new ScreenshotStore(screenshots);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(fixture, png);
        var screenshot = screenshotStore.Import(fixture);
        var written = new OcrHistoryStore(history).Save(new(recognition, File.ReadAllBytes(screenshotStore.PathFor(screenshot)), payload), "Pane OCR");
        screenshotStore.Delete(screenshot);
        var reopened = new OcrHistoryStore(history);
        var (entry, image) = reopened.Read(written.Id);
        if (!image.SequenceEqual(png) || entry.Text != "Pane OCR" || entry.Original.Hit(new(.5, .5)) is not { Start: 0, Length: 8 })
            throw new InvalidOperationException("Deleting the source screenshot affected archived OCR assets or geometry.");

        entry = reopened.Revise(entry, "Pane\r\nEdited");
        var next = reopened.Revise(entry, "Latest");
        var pending = reopened.Read(next.Id).Record;
        if (pending.Text != "Latest" || pending.Corrections[1].Text != "Pane\nEdited" || pending.Original.Text != "Pane OCR")
            throw new InvalidOperationException("Reopening a correction did not preserve the earlier and original text.");

        var correctionPath = Path.Combine(history, next.Id.ToString("N"), "corrections.json");
        using (var locked = File.Open(correctionPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                reopened.Revise(next, "Cannot publish");
                throw new InvalidOperationException("A locked correction was replaced.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (reopened.Read(next.Id).Record.Text != "Latest" || File.Exists(correctionPath + ".tmp"))
            throw new InvalidOperationException("Failed correction publication changed the active record or left temporary data.");

        reopened.Revise(pending, pending.Corrections[1].Text);
        var restored = new OcrHistoryStore(history).Read(next.Id);
        if (restored.Record.Text != "Pane\nEdited" || restored.Record.Corrections[1].Text != "Latest" || !restored.Image.SequenceEqual(png))
            throw new InvalidOperationException("Restoring a correction changed the original or lost the active version.");
        var resultPath = Path.Combine(history, next.Id.ToString("N"), OcrHistoryStore.ResultName);
        var deleted = false;
        using (var locked = File.Open(resultPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { reopened.Delete(next.Id); deleted = true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (deleted)
        {
            if (reopened.List().Any(record => record.Id == next.Id))
                throw new InvalidOperationException("An OCR record reported deleted was still listed.");
        }
        else
        {
            var retained = reopened.Read(next.Id);
            if (!retained.Image.SequenceEqual(png) || retained.Record.Text != restored.Record.Text ||
                !retained.Record.Corrections.SequenceEqual(restored.Record.Corrections))
                throw new InvalidOperationException("A rejected OCR deletion damaged the archived record.");
            reopened.Delete(next.Id);
        }

        var cleanup = reopened.Save(new(recognition, png, payload), recognition.Text);
        reopened.Delete(cleanup.Id);
        if (reopened.List().Length != 0 || Directory.EnumerateFileSystemEntries(history).Any())
            throw new InvalidOperationException("Deleting an OCR task left recoverable assets in the archive.");

        var readOnly = reopened.Save(new(recognition, png, payload), recognition.Text);
        var readOnlyPath = Path.Combine(history, readOnly.Id.ToString("N"), OcrHistoryStore.ResultName);
        File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);
        try
        {
            reopened.Delete(readOnly.Id);
            throw new InvalidOperationException("Deleting a read-only OCR record must fail.");
        }
        catch (UnauthorizedAccessException) { }
        finally { if (File.Exists(readOnlyPath)) File.SetAttributes(readOnlyPath, FileAttributes.Normal); }
        cleanup = reopened.Save(new(recognition, png, payload), recognition.Text);
        if (!reopened.Read(readOnly.Id).Image.SequenceEqual(png) || !reopened.List().Any(record => record.Id == cleanup.Id))
            throw new InvalidOperationException("Failed deletion or subsequent save changed the existing OCR record.");
        reopened.Delete(readOnly.Id);
        return 7;
    }
}
