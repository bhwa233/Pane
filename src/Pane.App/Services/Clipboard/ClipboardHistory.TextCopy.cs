using Pane.Contracts;
using Pane.Localization;

namespace Pane.Services.Clipboard;

internal sealed partial class ClipboardHistory
{
    internal const int TextCopyLimit = 1_000_000;
    const int DraftCountLimit = 16, DraftTotalLimit = 2 * TextCopyLimit;

    static bool HasText(ClipEntry entry) => entry.Kind is (ClipKind.Text or ClipKind.Code or ClipKind.Link or ClipKind.Color) &&
        entry.Text is { Length: > 0 };

    internal static bool CanEditText(ClipEntry entry) => HasText(entry) && entry.Text!.Length <= TextCopyLimit;

    public string? GetTextDraft(string id)
    {
        lock (sync) return entries.Find(clip => clip.Entry.Id == id)?.TextDraft;
    }

    public bool SetTextDraft(string id, string? text)
    {
        if (text is { } input && (input.Length > TextCopyLimit || input.Contains('\0'))) return false;
        lock (sync)
        {
            if (stopping || !Ready.IsCompletedSuccessfully) return false;
            var position = entries.FindIndex(clip => clip.Entry.Id == id);
            if (position < 0 || !CanEditText(entries[position].Entry)) return false;
            var original = entries[position];
            if (text != null && SameText(text, original.Entry.Text!)) text = null;
            if (original.TextDraft == text) return true;
            if (text != null)
            {
                int remaining = DraftTotalLimit - text.Length, available = DraftCountLimit - 1;
                foreach (var clip in entries)
                    if (!ReferenceEquals(clip, original) && clip.TextDraft is { } draft && (--available < 0 || (remaining -= draft.Length) < 0)) return false;
            }
            entries[position] = original with { TextDraft = text };
            Schedule();
            return true;
        }
    }

    static bool SameText(string text, string original) => text == original ||
        text.ReplaceLineEndings("\n") == original.ReplaceLineEndings("\n");

    internal static string? ExportExtension(ClipEntry entry) => HasText(entry) ? ".txt" :
        entry is { Kind: ClipKind.Image, ContentPath.Length: > 0 } ? ".png" : null;

    public Task ExportFileAsync(string id, string path, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        ClipEntry entry;
        string imageRoot;
        lock (sync)
        {
            if (stopping || entries.Find(clip => clip.Entry.Id == id) is not { Entry: { } found })
                throw new InvalidDataException();
            entry = found;
            imageRoot = imageDirectory;
        }
        var extension = ExportExtension(entry) ?? throw new InvalidDataException();
        path = Path.GetFullPath(path);
        var parent = StorageLocation.ResolvePath(Path.GetDirectoryName(path)!);
        var name = Path.GetFileName(path);
        if (name.Contains(':') || !Path.GetExtension(name).Equals(extension, StringComparison.OrdinalIgnoreCase) ||
            StorageLocation.Within(parent, StorageLocation.ResolvePath(settings.DirectoryPath)) ||
            StorageLocation.Within(parent, StorageLocation.ResolvePath(imageRoot))) throw new InvalidDataException();
        var temporary = Path.Combine(parent, $".pane-clipboard-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                if (extension == ".png")
                {
                    using var image = File.OpenRead(entry.ContentPath!);
                    await image.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    using var writer = new StreamWriter(stream);
                    writer.Write(entry.Text);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(parent, name), overwrite: true);
        }
        finally { File.Delete(temporary); }
    }, cancellationToken);

    public ClipTextCopyResult SaveTextCopy(string id, string text)
    {
        if (text.Length > TextCopyLimit || string.IsNullOrWhiteSpace(text) || text.Contains('\0'))
            return ClipTextCopyResult.InvalidText;
        var fingerprint = ClipboardTextFormats.Fingerprint(text, null);
        var kind = ClipboardClassifier.Classify(text);
        lock (sync)
        {
            var options = settings.Current.Clipboard;
            if (stopping || !Ready.IsCompletedSuccessfully || !options.Enabled) return ClipTextCopyResult.Unavailable;
            var position = entries.FindIndex(clip => clip.Entry.Id == id);
            if (position < 0 || !CanEditText(entries[position].Entry))
                return ClipTextCopyResult.OriginalUnavailable;
            var original = entries[position].Entry;
            if (SameText(text, original.Text!)) return ClipTextCopyResult.InvalidText;
            var copy = new ClipEntry(Guid.NewGuid().ToString("N"), kind, text, null, null, null, null,
                Strings.Clipboard_TextCopySource, null, DateTimeOffset.UtcNow, false);
            var updated = new List<StoredClip>(entries) { new(copy, fingerprint) };
            updated[position] = updated[position] with { TextDraft = null };
            ClipboardCollections.TrimHistory(updated, options.Limit);
            if (!updated.Exists(clip => ReferenceEquals(clip.Entry, original)) || !updated.Exists(clip => ReferenceEquals(clip.Entry, copy)))
                return ClipTextCopyResult.HistoryFull;
            entries.Clear();
            entries.AddRange(updated);
            Schedule();
        }
        NotifyChanged();
        return ClipTextCopyResult.Saved;
    }
}
