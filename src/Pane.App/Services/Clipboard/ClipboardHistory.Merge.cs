using System.Text;
using Pane.Contracts;
using Pane.Localization;

namespace Pane.Services.Clipboard;

internal sealed partial class ClipboardHistory
{
    public async Task<ClipMergeResult> MergeAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count is < 2 or > ClipboardMerge.MaxItems || ids.Distinct().Count() != ids.Count)
            return ClipMergeResult.InvalidSelection;
        await Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        ClipEntry?[] selected;
        lock (sync)
        {
            if (stopping || !settings.Current.Clipboard.Enabled) return ClipMergeResult.Unavailable;
            selected = ids.Select(id => entries.Find(clip => clip.Entry.Id == id)?.Entry).ToArray();
        }

        var (status, result) = await Task.Run(() => ClipboardMerge.Build(selected, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        if (result is null) return status;
        var fingerprint = result.Files is { } files
            ? "f:" + Hash(Encoding.UTF8.GetBytes(string.Join('\0', files)))
            : ClipboardTextFormats.Fingerprint(result.Text!, result.RichText);

        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopping || !settings.Current.Clipboard.Enabled) return ClipMergeResult.Unavailable;
            if (selected.Any(item => !entries.Exists(clip => clip.Entry.Id == item!.Id && clip.Entry.ContentPath == item.ContentPath)))
                return ClipMergeResult.InvalidSelection;
            if (result.RichText is { ImageMerge: true, Html: { } html } &&
                ClipboardTextFormats.ImageMergeBytes(entries) + html.Length > ClipboardTextFormats.ImageMergeHistoryByteLimit)
                return ClipMergeResult.StorageFull;
            entries.Add(new(result with { CopiedAt = DateTimeOffset.UtcNow }, fingerprint));
            Trim();
            Schedule();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return ClipMergeResult.Saved;
    }
}

internal static class ClipboardMerge
{
    internal const int MaxItems = 20;
    const int MaxTextLength = 1_000_000;
    const int MaxFileCount = 500;

    internal static bool IsCompatible(ClipEntry entry) => entry.Kind switch
    {
        ClipKind.Files => entry.Files is { Count: > 0 },
        ClipKind.Image => !string.IsNullOrEmpty(entry.ContentPath),
        _ => !string.IsNullOrEmpty(entry.Text) && entry.RichText?.ImageMerge != true,
    };

    internal static (ClipMergeResult Status, ClipEntry? Entry) Build(IReadOnlyList<ClipEntry?> selected, CancellationToken token)
    {
        if (selected.Count is < 2 or > MaxItems || selected[0] is not { } first)
            return (ClipMergeResult.InvalidSelection, null);
        var isFiles = first.Kind == ClipKind.Files;
        if (selected.Any(clip => clip is null || !IsCompatible(clip) || (clip.Kind == ClipKind.Files) != isFiles))
            return (ClipMergeResult.InvalidSelection, null);

        if (isFiles)
        {
            if (selected.Sum(clip => (long)clip!.Files!.Count) > MaxFileCount) return (ClipMergeResult.TooLarge, null);
            var files = selected.SelectMany(clip => clip!.Files!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var path in files)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || (!File.Exists(path) && !Directory.Exists(path)))
                    return (ClipMergeResult.FileUnavailable, null);
            }
            return (ClipMergeResult.Saved, NewEntry(ClipKind.Files, null, files));
        }

        var lines = selected.Select(clip => clip!.Kind == ClipKind.Image ? $"[{Strings.Common_Image}]" : clip.Text!).ToArray();
        var length = lines.Sum(line => (long)line.Length) + (lines.Length - 1) * Environment.NewLine.Length;
        if (length > MaxTextLength) return (ClipMergeResult.TooLarge, null);
        var text = string.Join(Environment.NewLine, lines);
        if (selected.Any(clip => clip!.Kind == ClipKind.Image)) return BuildImageMerge(selected, lines, text, token);
        return (ClipMergeResult.Saved, NewEntry(ClipboardClassifier.Classify(text), text, null));
    }

    static (ClipMergeResult, ClipEntry?) BuildImageMerge(IReadOnlyList<ClipEntry?> selected, string[] lines, string text, CancellationToken token)
    {
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        static string Header(int start, int end, int first, int last) => FormattableString.Invariant(
            $"Version:1.0\r\nStartHTML:{start:D10}\r\nEndHTML:{end:D10}\r\nStartFragment:{first:D10}\r\nEndFragment:{last:D10}\r\n");
        var headerSize = Header(0, 0, 0, 0).Length;
        int remaining = ClipboardTextFormats.ImageMergeByteLimit - headerSize - prefix.Length - suffix.Length;
        var fragment = new StringBuilder();
        for (var index = 0; index < selected.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            string paragraph;
            if (selected[index]!.Kind == ClipKind.Image)
            {
                const string imageStart = "<p><img src=\"data:image/png;base64,";
                const string imageEnd = "\" alt=\"\"></p>";
                int budget = remaining - imageStart.Length - imageEnd.Length;
                if (budget < 4) return (ClipMergeResult.TooLarge, null);
                try
                {
                    using var input = File.OpenRead(selected[index]!.ContentPath!);
                    if (input.Length > budget / 4 * 3L) return (ClipMergeResult.TooLarge, null);
                    var bytes = new byte[(int)input.Length];
                    input.ReadExactly(bytes);
                    if (!bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                        return (ClipMergeResult.ImageUnavailable, null);
                    paragraph = imageStart + Convert.ToBase64String(bytes) + imageEnd;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return (ClipMergeResult.ImageUnavailable, null);
                }
            }
            else
            {
                paragraph = "<p>" + System.Net.WebUtility.HtmlEncode(lines[index].Replace('\0', '\uFFFD')).ReplaceLineEndings("<br>") + "</p>";
            }
            remaining -= Encoding.UTF8.GetByteCount(paragraph);
            if (remaining < 0) return (ClipMergeResult.TooLarge, null);
            fragment.Append(paragraph);
        }
        var startFragment = headerSize + prefix.Length;
        var endFragment = ClipboardTextFormats.ImageMergeByteLimit - suffix.Length - remaining;
        var html = Encoding.UTF8.GetBytes(Header(headerSize, endFragment + suffix.Length, startFragment, endFragment) + prefix + fragment + suffix);
        return (ClipMergeResult.Saved, NewEntry(ClipKind.Text, text, null) with { RichText = new(html, null, ImageMerge: true) });
    }

    static ClipEntry NewEntry(ClipKind kind, string? text, string[]? files) => new(Guid.NewGuid().ToString("N"), kind,
        text, files, null, null, null, Strings.Clipboard_Merge, null, DateTimeOffset.UtcNow, false);
}
