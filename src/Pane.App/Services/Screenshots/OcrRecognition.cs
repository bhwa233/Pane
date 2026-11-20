using System.Text.Json;

namespace Pane.Services.Screenshots;

internal readonly record struct OcrPoint(double X, double Y);

internal readonly record struct OcrQuad(OcrPoint First, OcrPoint Second, OcrPoint Third, OcrPoint Fourth)
{
    internal bool Contains(OcrPoint point) =>
        Cross(First, Second, point) >= 0 && Cross(Second, Third, point) >= 0 &&
        Cross(Third, Fourth, point) >= 0 && Cross(Fourth, First, point) >= 0;

    internal static OcrQuad? Read(JsonElement item, (uint Width, uint Height) size)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("box", out var box) ||
            box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 4) return null;
        Span<OcrPoint> points = stackalloc OcrPoint[4];
        for (var index = 0; index < points.Length; index++)
        {
            var pair = box[index];
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2 ||
                pair[0].ValueKind != JsonValueKind.Number || pair[1].ValueKind != JsonValueKind.Number ||
                !pair[0].TryGetDouble(out var x) || !pair[1].TryGetDouble(out var y) ||
                !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > size.Width || y < 0 || y > size.Height) return null;
            points[index] = new(x, y);
        }
        var direction = Math.Sign(Cross(points[0], points[1], points[2]));
        if (direction == 0 || direction != Math.Sign(Cross(points[1], points[2], points[3])) ||
            direction != Math.Sign(Cross(points[2], points[3], points[0])) ||
            direction != Math.Sign(Cross(points[3], points[0], points[1]))) return null;
        return direction > 0 ? new(points[0], points[1], points[2], points[3]) : new(points[0], points[3], points[2], points[1]);
    }

    static double Cross(OcrPoint from, OcrPoint to, OcrPoint point) =>
        (to.X - from.X) * (point.Y - from.Y) - (to.Y - from.Y) * (point.X - from.X);
}

internal readonly record struct OcrSpan(int Start, int Length, OcrQuad Quad);
internal sealed record OcrLine(OcrSpan? Whole, OcrSpan[] Characters);

internal sealed record OcrRecognition(string Text, uint Width, uint Height, OcrLine[] Lines)
{
    internal OcrSpan? Hit(OcrPoint point)
    {
        foreach (var line in Lines)
            foreach (var character in line.Characters)
                if (character.Quad.Contains(point)) return character;
        foreach (var line in Lines)
            if (line is { Characters.Length: 0, Whole: { } whole } && whole.Quad.Contains(point)) return whole;
        return null;
    }

    internal (int Start, int Length)? DisplayRange(string displayed, OcrSpan span)
        => DisplayRange(displayed, span, span);

    internal (int Start, int Length)? DisplayRange(string displayed, OcrSpan anchor, OcrSpan extent)
    {
        var first = Math.Min(anchor.Start, extent.Start);
        var last = Math.Max(anchor.Start + anchor.Length, extent.Start + extent.Length);
        if (Text == displayed) return (first, last - first);
        var start = -1;
        var end = -1;
        var offset = 0;
        for (var index = 0; index < Text.Length; index++)
        {
            if (index == first) start = offset;
            if (index == last) end = offset;
            if (offset >= displayed.Length) return null;
            if (Text[index] == '\n' && displayed[offset] == '\r')
            {
                offset++;
                if (offset < displayed.Length && displayed[offset] == '\n') offset++;
            }
            else if (Text[index] != displayed[offset++]) return null;
        }
        if (Text.Length == last) end = offset;
        return offset == displayed.Length && start >= 0 && end > start ? (start, end - start) : null;
    }

    internal IEnumerable<OcrQuad> HighlightRange(OcrSpan anchor, OcrSpan extent)
    {
        if (anchor == extent)
        {
            yield return anchor.Quad;
            yield break;
        }
        var first = Math.Min(anchor.Start, extent.Start);
        var last = Math.Max(anchor.Start + anchor.Length, extent.Start + extent.Length);
        foreach (var line in Lines)
        {
            if (line.Whole is { } whole &&
                (line.Characters.Length == 0 ? whole.Start < last && whole.Start + whole.Length > first :
                    whole.Start >= first && whole.Start + whole.Length <= last))
            {
                yield return whole.Quad;
                continue;
            }

            OcrQuad? selected = null;
            var left = double.PositiveInfinity;
            var top = double.PositiveInfinity;
            var right = double.NegativeInfinity;
            var bottom = double.NegativeInfinity;
            var count = 0;
            foreach (var character in line.Characters)
            {
                if (character.Start >= last) break;
                if (character.Start + character.Length <= first) continue;
                selected = character.Quad;
                count++;
                Include(character.Quad.First);
                Include(character.Quad.Second);
                Include(character.Quad.Third);
                Include(character.Quad.Fourth);
            }
            if (count == 1 && selected is { } quad) yield return quad;
            else if (count > 1) yield return new(new(left, top), new(right, top), new(right, bottom), new(left, bottom));

            void Include(OcrPoint point)
            {
                left = Math.Min(left, point.X);
                top = Math.Min(top, point.Y);
                right = Math.Max(right, point.X);
                bottom = Math.Max(bottom, point.Y);
            }
        }
    }

    internal static OcrRecognition Read(string text, JsonElement result, (uint Width, uint Height) size)
    {
        text = text.ReplaceLineEndings("\n");
        if (!result.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array ||
            lines.GetArrayLength() > 1_000) return new(text, size.Width, size.Height, []);
        List<OcrLine> located = [];
        var offset = 0;
        var budget = 8_000;
        foreach (var line in lines.EnumerateArray())
        {
            while (offset < text.Length && text[offset] == '\n') offset++;
            if (ReadText(line) is not { Length: > 0 } value || value.Contains('\n') || value.Contains('\r') ||
                !text.AsSpan(offset).StartsWith(value, StringComparison.Ordinal)) return new(text, size.Width, size.Height, []);
            var next = offset + value.Length;
            if (next < text.Length && text[next] != '\n') return new(text, size.Width, size.Height, []);
            OcrSpan? whole = OcrQuad.Read(line, size) is { } quad ? new(offset, value.Length, quad) : null;
            var characters = ReadCharacters(line, size, value, offset, ref budget);
            if (whole != null || characters.Length != 0) located.Add(new(whole, characters));
            offset = next;
        }
        if (!text.AsSpan(offset).Trim().IsEmpty) located.Clear();
        return new(text, size.Width, size.Height, located.ToArray());
    }

    static OcrSpan[] ReadCharacters(JsonElement line, (uint Width, uint Height) size, string value, int start, ref int budget)
    {
        if (!line.TryGetProperty("characters", out var items) || items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() > budget) return [];
        budget -= items.GetArrayLength();
        List<OcrSpan> spans = [];
        var offset = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (ReadText(item) is not { Length: > 0 } glyph || OcrQuad.Read(item, size) is not { } quad) return [];
            if (!value.AsSpan(offset).StartsWith(glyph, StringComparison.Ordinal))
                while (offset < value.Length && char.IsWhiteSpace(value[offset])) offset++;
            if (!value.AsSpan(offset).StartsWith(glyph, StringComparison.Ordinal) ||
                (offset > 0 && char.IsLowSurrogate(value[offset])) ||
                (offset + glyph.Length < value.Length && char.IsLowSurrogate(value[offset + glyph.Length]))) return [];
            spans.Add(new(start + offset, glyph.Length, quad));
            offset += glyph.Length;
        }
        return value.AsSpan(offset).Trim().IsEmpty ? spans.ToArray() : [];
    }

    static string? ReadText(JsonElement item) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
