using System.Text.Json;

namespace Pane.Services.Screenshots;

internal sealed record OcrImageResult(OcrRecognition Recognition, byte[] Image, byte[] Payload);

internal sealed class NativeScreenshotOcr(ScreenshotStore store, Func<byte[], (uint Width, uint Height), CancellationToken, Task<byte[]>> recognize)
{
    internal const int MaxResultBytes = 8 * 1024 * 1024;
    internal const int MaxTextLength = 250_000;
    Task? active;

    internal Task<OcrImageResult> RecognizeAsync(Guid id, CancellationToken token) =>
        RecognizeAsync(() => store.ReadOcrImage(id), token);

    internal Task<OcrImageResult> RecognizeAsync(byte[] png, CancellationToken token) =>
        RecognizeAsync(() => png, token);

    async Task<OcrImageResult> RecognizeAsync(Func<byte[]> load, CancellationToken token)
    {
        var previous = active;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        active = drained.Task;
        try
        {
            if (previous != null) await previous.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var image = await Task.Run(load, token).ConfigureAwait(false);
            using var input = new MemoryStream(image, writable: false);
            var size = ScreenshotStore.ReadSize(input);
            if ((ulong)size.Width * size.Height > ScreenshotStore.MaxOcrPixels)
                throw new InvalidDataException("Screenshot pixel count exceeds the OCR limit.");
            var payload = await recognize(image, size, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            return new(ReadRecognition(document.RootElement, size), image, payload);
        }
        finally { drained.SetResult(); }
    }

    internal Task DrainAsync() => active?.WaitAsync(TimeSpan.FromSeconds(5)) ?? Task.CompletedTask;

    internal static OcrRecognition ReadResult(string directory, string session, int pid, (uint Width, uint Height) size)
        => ReadResult(directory, session, pid, size, out _);

    static OcrRecognition ReadResult(string directory, string session, int pid, (uint Width, uint Height) size, out byte[] payload)
    {
        using var stream = ScreenshotStore.OpenSessionFile(directory, "ocr.json", MaxResultBytes);
        payload = new byte[checked((int)stream.Length)];
        stream.ReadExactly(payload);
        using var document = JsonDocument.Parse(payload);
        var result = document.RootElement;
        if (!NativeScreenshotCapture.MatchesSession(result, session, pid, "ocr"))
            throw new InvalidDataException("Native OCR result does not match the active image and session.");
        return ReadRecognition(result, size);
    }

    internal static OcrRecognition ReadRecognition(JsonElement result, (uint Width, uint Height) size)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("width", out var width) || width.ValueKind != JsonValueKind.Number ||
            !width.TryGetUInt32(out var declaredWidth) || declaredWidth != size.Width || !result.TryGetProperty("height", out var height) ||
            height.ValueKind != JsonValueKind.Number || !height.TryGetUInt32(out var declaredHeight) || declaredHeight != size.Height ||
            !result.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: <= MaxTextLength } text)
            throw new InvalidDataException("Invalid OCR result for the image.");
        return OcrRecognition.Read(text, result, size);
    }
}
