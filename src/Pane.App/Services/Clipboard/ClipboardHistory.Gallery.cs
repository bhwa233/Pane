using System.Security.Cryptography;
using Pane.Contracts;
using Pane.Localization;

namespace Pane.Services.Clipboard;

internal sealed partial class ClipboardHistory
{
    internal const int GalleryBatchLimit = 32;
    const int GalleryByteLimit = 32 * 1024 * 1024;
    const uint GalleryPixelLimit = 16_000_000;

    public async Task<ClipGalleryImportResult> ImportGalleryAsync(IReadOnlyList<string> paths, string collection, CancellationToken cancellationToken)
    {
        if (paths.Count > GalleryBatchLimit || !ClipboardCollections.Valid([collection])) return new(0, 0, paths.Count);
        await Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => ImportGallery(paths, collection, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    ClipGalleryImportResult ImportGallery(IReadOnlyList<string> paths, string collection, CancellationToken token)
    {
        int added = 0, existing = 0, failed = 0;
        try
        {
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!WindowsClipboardImages.LocalImage(path)) { failed++; continue; }
                    using var input = File.OpenRead(path);
                    if (input.Length is <= 0 or > GalleryByteLimit) { failed++; continue; }
                    var bytes = new byte[checked((int)input.Length)];
                    input.ReadExactly(bytes);
                    switch (AddGalleryImage(bytes, collection, token))
                    {
                        case true: added++; break;
                        case false: existing++; break;
                        default: failed++; break;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException)
                {
                    failed++;
                    log.Write("gallery-import-failed", error);
                }
            }
            return new(added, existing, failed);
        }
        finally { if (added > 0) NotifyChanged(); }
    }

    bool? AddGalleryImage(byte[] bytes, string collection, CancellationToken token)
    {
        bool? AddMembership(string fingerprint)
        {
            var matching = entries.FindIndex(clip => clip.Fingerprint == fingerprint && File.Exists(clip.Entry.ContentPath));
            if (matching < 0) return null;
            var clip = entries[matching];
            if (clip.Entry.Collections?.Contains(collection, ClipboardCollections.NameComparer) == true) return false;
            var groups = ClipboardCollections.Combine(clip.Entry.Collections, [collection]);
            entries[matching] = clip with { Entry = clip.Entry with { Collections = groups } };
            Schedule();
            return true;
        }

        lock (settings.StorageSync)
        {
            string inputFingerprint = "i:" + Hash(bytes);
            string id = Guid.NewGuid().ToString("N");
            string full, thumb;
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                if (stopping || !settings.Current.Clipboard.Enabled) return null;
                if (AddMembership(inputFingerprint) is { } attached) return attached;
                full = Path.Combine(imageDirectory, id + ".png");
                thumb = Path.Combine(imageDirectory, id + ".thumb.png");
            }

            bool saved = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var size = images.SaveGallery(bytes, full, thumb, GalleryPixelLimit);
                if (FileBytes(full) is <= 0 or > GalleryByteLimit || FileBytes(thumb) <= 0) throw new InvalidDataException();
                string fingerprint;
                using (var savedImage = File.OpenRead(full)) fingerprint = "i:" + Convert.ToHexString(SHA256.HashData(savedImage));
                lock (sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (stopping || !settings.Current.Clipboard.Enabled) return null;
                    if ((AddMembership(fingerprint) ?? (fingerprint != inputFingerprint ? AddMembership(inputFingerprint) : null)) is { } attached)
                        return attached;
                    entries.Add(new(new(id, ClipKind.Image, null, null, new(thumb), size.Width, size.Height,
                        Strings.Clipboard_GallerySource, null, DateTimeOffset.UtcNow, false, full, Collections: [collection], Archived: true), fingerprint));
                    saved = true;
                    Schedule();
                    return true;
                }
            }
            finally
            {
                if (!saved)
                {
                    try { File.Delete(full); File.Delete(thumb); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log.Write("gallery-cleanup-failed", error); }
                }
            }
        }
    }
}
