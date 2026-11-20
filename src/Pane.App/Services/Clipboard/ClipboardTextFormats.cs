using System.Security.Cryptography;
using System.Text;
using Pane.Contracts;

namespace Pane.Services.Clipboard;

internal static class ClipboardTextFormats
{
    internal const int FormatByteLimit = 256 * 1024;
    internal const int HistoryByteLimit = 8 * 1024 * 1024;
    internal const int ImageMergeByteLimit = 8 * 1024 * 1024;
    internal const int ImageMergeHistoryByteLimit = 16 * 1024 * 1024;

    internal static ClipRichText? Normalize(ClipRichText? formats)
    {
        if (formats is null) return null;
        if (formats.ImageMerge) return Valid(formats) ? formats : null;
        var html = ValidPayload(formats.Html) ? formats.Html : null;
        var rtf = ValidRtf(formats.Rtf) ? formats.Rtf : null;
        return html is null && rtf is null ? null : new(html, rtf);
    }

    internal static bool Valid(ClipRichText? formats) => formats is null || (formats.ImageMerge
        ? formats.Rtf is null && ValidPayload(formats.Html, ImageMergeByteLimit)
        :
        (formats.Html is not null || formats.Rtf is not null) &&
        (formats.Html is null || ValidPayload(formats.Html)) && (formats.Rtf is null || ValidRtf(formats.Rtf)));

    static bool ValidPayload(byte[]? bytes, int limit = FormatByteLimit) => bytes is { Length: > 0 } && bytes.Length <= limit && !bytes.AsSpan().Contains((byte)0);

    internal static long ImageMergeBytes(IEnumerable<StoredClip> entries) => entries.Sum(clip =>
        clip.Entry.RichText is { ImageMerge: true, Html: { } html } ? (long)html.Length : 0L);

    static bool ValidRtf(byte[]? bytes)
    {
        if (bytes is null || !ValidPayload(bytes) || !bytes.AsSpan().StartsWith("{\\rtf"u8)) return false;
        var end = bytes.Length - 1;
        while (bytes[end] is 9 or 10 or 13 or 32) end--;
        return bytes[end] == (byte)'}';
    }

    internal static string Fingerprint(string text, ClipRichText? formats)
    {
        var plain = "t:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (formats is null) return plain;
        var fingerprint = $"{plain}:{Hash(formats.Html)}:{Hash(formats.Rtf)}";
        return formats.ImageMerge ? fingerprint + ":image-merge" : fingerprint;
    }

    static string Hash(byte[]? bytes) => bytes is null ? "" : Convert.ToHexString(SHA256.HashData(bytes));

    internal static bool Trim(List<StoredClip> entries)
    {
        long total = 0;
        foreach (var clip in entries)
            if (clip.Entry.RichText is { ImageMerge: false } richText) total += Size(richText);
        if (total <= HistoryByteLimit) return false;

        var formats = entries.Select((clip, index) => (clip.Entry, Index: index)).Where(item => item.Entry.RichText is { ImageMerge: false }).ToArray();
        int available = HistoryByteLimit;
        foreach (var (entry, index) in formats.OrderByDescending(item => item.Entry.Pinned).ThenByDescending(item => item.Entry.CopiedAt))
        {
            var size = Size(entry.RichText!);
            if (size <= available) { available -= size; continue; }
            entries[index] = entries[index] with { Entry = entry with { RichText = null }, Fingerprint = Fingerprint(entry.Text ?? "", null) };
        }
        return true;
    }

    static int Size(ClipRichText formats) => (formats.Html?.Length ?? 0) + (formats.Rtf?.Length ?? 0);
}
