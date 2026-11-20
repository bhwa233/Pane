using Pane.Services.Screenshots;

static class OcrResultTests
{
    internal static int Run()
    {
        var directory = Directory.CreateTempSubdirectory("pane-ocr-result-");
        var result = Path.Combine(directory.FullName, "ocr.json");
        var captureResult = Path.Combine(directory.FullName, "capture.json");
        var selection = Path.Combine(directory.FullName, "selection.png");
        try
        {
            var session = Guid.NewGuid().ToString("N");
            File.WriteAllText(result, $$"""{"protocol":1,"sessionId":"{{session}}","pid":481,"kind":"ocr","width":120,"height":60,"text":"Pane\n123"}""");
            if (NativeScreenshotOcr.ReadResult(directory.FullName, session, 481, (120, 60)).Text != "Pane\n123")
                throw new InvalidOperationException("OCR lost the newline between recognized lines.");

            ExpectRejected(session, 482, (120, 60));
            ExpectRejected(Guid.NewGuid().ToString("N"), 481, (120, 60));
            ExpectRejected(session, 481, (60, 120));
            File.WriteAllBytes(selection, []);
            WriteCaptureResult("ocr", session);
            if (NativeScreenshotCapture.ReadSelection(directory.FullName, session, 481, true) != (120u, 60u, true))
                throw new InvalidOperationException("An OCR-enabled capture did not request recognition.");
            ExpectCaptureRejected(session);
            WriteCaptureResult("save", session);
            if (NativeScreenshotCapture.ReadSelection(directory.FullName, session, 481, false) != (120u, 60u, false))
                throw new InvalidOperationException("Saving a capture must not depend on OCR.");
            File.WriteAllText(result, $$"""
                {"protocol":1,"sessionId":"{{session}}","pid":481,"kind":"ocr","width":120,"height":60,
                 "text":"\u4e2d\nA 5\ud83d\ude42\nLine","lines":[
                  {"text":"\u4e2d","box":[[10,4],[30,4],[29,14],[9,14]]},
                  {"text":"A 5\ud83d\ude42","box":[[10,19],[51,20],[50,35],[9,34]],"characters":[
                    {"text":"A","box":[[10,19],[20,19],[19,34],[9,34]]},
                    {"text":"5","box":[[30,20],[40,20],[39,35],[29,35]]},
                    {"text":"\ud83d\ude42","box":[[41,20],[40,35],[50,35],[51,20]]}]},
                  {"text":"Line","box":[[10,40],[60,40],[60,54],[10,54]],"characters":[
                    {"text":"L","box":[[10,40],[10,40],[19,54],[10,54]]}]}]}
                """);
            var geometry = NativeScreenshotOcr.ReadResult(directory.FullName, session, 481, (120, 60));
            if (geometry.Hit(new(15, 8)) is not { Start: 0, Length: 1 } ||
                geometry.Hit(new(35, 27)) is not { Start: 4, Length: 1 } ||
                geometry.Hit(new(45, 27)) is not { Start: 5, Length: 2 } character)
                throw new InvalidOperationException("OCR image coordinates did not resolve to the expected text ranges.");
            if (geometry.DisplayRange(geometry.Text.Replace("\n", "\r\n"), character) != (6, 2) ||
                geometry.DisplayRange("replaced", character) != null)
                throw new InvalidOperationException("OCR selection did not account for displayed line endings or mismatched text.");
            if (geometry.Hit(new(45, 47)) is not { Start: 8, Length: 4 } ||
                geometry.Hit(new(25, 27)) != null || geometry.Hit(new(100, 52)) != null)
                throw new InvalidOperationException("OCR did not limit line fallback and ignore unrecognized space.");
            var endLine = geometry.Hit(new(45, 47))!.Value;
            var startCharacter = geometry.Hit(new(35, 27))!.Value;
            var displayed = geometry.Text.Replace("\n", "\r\n");
            if (geometry.DisplayRange(displayed, endLine, startCharacter) is not { } selectedText ||
                displayed.Substring(selectedText.Start, selectedText.Length) != "5\ud83d\ude42\r\nLine" ||
                geometry.DisplayRange(displayed, startCharacter, endLine) != selectedText)
                throw new InvalidOperationException("OCR range selection lost a line break, reversed endpoints or split a supplementary character.");
            var highlights = geometry.HighlightRange(endLine, startCharacter).ToArray();
            if (highlights is not [var partial, var fallback] || partial.Contains(new(15, 27)) ||
                !partial.Contains(new(35, 27)) || !partial.Contains(new(45, 27)) || fallback != endLine.Quad)
                throw new InvalidOperationException("OCR highlights must follow the selected characters and whole-line fallback.");
            var mismatch = File.ReadAllText(result).Replace("\"text\":\"Line\"", "\"text\":\"Other\"", StringComparison.Ordinal);
            File.WriteAllText(result, mismatch);
            var unrelated = NativeScreenshotOcr.ReadResult(directory.FullName, session, 481, (120, 60));
            if (unrelated.Text != geometry.Text || unrelated.Hit(new(15, 8)) != null)
                throw new InvalidOperationException("Mismatched OCR annotations must not be applied to the text.");
            return 13;
        }
        finally
        {
            File.Delete(result);
            File.Delete(captureResult);
            File.Delete(selection);
            directory.Delete();
        }

        void ExpectRejected(string session, int pid, (uint Width, uint Height) size)
        {
            try { NativeScreenshotOcr.ReadResult(directory.FullName, session, pid, size); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("OCR accepted a result for the wrong session, process or image size.");
        }

        void WriteCaptureResult(string action, string session) =>
            File.WriteAllText(captureResult, $$"""{"protocol":1,"sessionId":"{{session}}","pid":481,"kind":"capture","action":"{{action}}","image":"selection.png","width":120,"height":60}""");

        void ExpectCaptureRejected(string session)
        {
            try { NativeScreenshotCapture.ReadSelection(directory.FullName, session, 481, false); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("A capture with OCR disabled accepted an OCR action.");
        }
    }
}
