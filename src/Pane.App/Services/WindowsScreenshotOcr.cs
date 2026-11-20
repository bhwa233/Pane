using System.Buffers;
using System.Text.Json;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Pane.Services.Screenshots;

internal static class WindowsScreenshotOcr
{
    internal static async Task<byte[]> RecognizeAsync(byte[] image, (uint Width, uint Height) size, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null && OcrEngine.AvailableRecognizerLanguages.FirstOrDefault() is { } installed)
            engine = OcrEngine.TryCreateFromLanguage(installed);
        if (engine is null) throw new InvalidOperationException(Localization.Strings.Screenshots_WindowsOcrLanguageMissing);

        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input))
        {
            writer.WriteBytes(image);
            await writer.StoreAsync().AsTask(token).ConfigureAwait(false);
            writer.DetachStream();
        }
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(token).ConfigureAwait(false);
        var maxEdge = Math.Max(size.Width, size.Height);
        var scale = Math.Min(maxEdge < 1_000 ? 2.0 : 1.0, (double)OcrEngine.MaxImageDimension / maxEdge);
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(size.Width * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(size.Height * scale)),
            InterpolationMode = BitmapInterpolationMode.Linear,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(token).ConfigureAwait(false);
        var result = await engine.RecognizeAsync(bitmap).AsTask(token).ConfigureAwait(false);
        return WriteResult(result, size, size.Width / (double)bitmap.PixelWidth, size.Height / (double)bitmap.PixelHeight);
    }

    static byte[] WriteResult(OcrResult result, (uint Width, uint Height) size, double scaleX, double scaleY)
    {
        var lines = result.Lines.Where(line => line.Words.Count > 0).ToArray();
        var output = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject();
            json.WriteNumber("width", size.Width);
            json.WriteNumber("height", size.Height);
            json.WriteString("text", string.Join('\n', lines.Select(line => line.Text)));
            json.WriteStartArray("lines");
            foreach (var line in lines)
            {
                var words = line.Words;
                json.WriteStartObject();
                json.WriteString("text", line.Text);
                var left = words.Min(word => word.BoundingRect.Left);
                var top = words.Min(word => word.BoundingRect.Top);
                var right = words.Max(word => word.BoundingRect.Right);
                var bottom = words.Max(word => word.BoundingRect.Bottom);
                WriteBox(json, new(left, top, right - left, bottom - top), size, scaleX, scaleY);
                json.WriteStartArray("characters");
                foreach (var word in words)
                {
                    json.WriteStartObject();
                    json.WriteString("text", word.Text);
                    WriteBox(json, word.BoundingRect, size, scaleX, scaleY);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }

    static void WriteBox(Utf8JsonWriter json, Rect box, (uint Width, uint Height) size, double scaleX, double scaleY)
    {
        var left = Math.Clamp(box.Left * scaleX, 0, size.Width);
        var right = Math.Clamp(box.Right * scaleX, left, size.Width);
        var top = Math.Clamp(box.Top * scaleY, 0, size.Height);
        var bottom = Math.Clamp(box.Bottom * scaleY, top, size.Height);
        json.WriteStartArray("box");
        Point(left, top);
        Point(right, top);
        Point(right, bottom);
        Point(left, bottom);
        json.WriteEndArray();

        void Point(double x, double y)
        {
            json.WriteStartArray();
            json.WriteNumberValue(x);
            json.WriteNumberValue(y);
            json.WriteEndArray();
        }
    }
}
