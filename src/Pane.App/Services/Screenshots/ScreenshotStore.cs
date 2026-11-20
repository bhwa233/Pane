using System.Buffers.Binary;
using System.Text.Json;
using Pane.Services.Clipboard;

namespace Pane.Services.Screenshots;

internal sealed record ScreenshotEntry(Guid Id, DateTime CreatedUtc);
internal readonly record struct ScreenshotEdit(Guid Id, Guid Revision, Guid? Previous);

internal sealed class ScreenshotStore(string directory, object? storageSync = null)
{
    internal const int DesktopPinLimit = 8;
    internal const int UnpinnedHistoryLimit = 50;
    internal const int MaxFileBytes = 64 * 1024 * 1024;
    internal const int MaxDimension = 16000;
    internal const uint MaxPixels = 64_000_000;
    internal const uint MaxOcrPixels = 24_000_000;
    internal const long MaxDocumentBytes = 8 * 1024 * 1024;
    const string DeletedSuffix = ".pin.deleted";
    readonly object gate = storageSync ?? new();
    string EditableDirectory(Guid id) => Path.Combine(directory, id.ToString("N") + ".pin");
    internal static string RevisionDirectory(string folder, Guid? revision) =>
        revision is { } value ? Path.Combine(folder, value.ToString("N")) : folder;

    internal string PathFor(Guid id)
    {
        var folder = EditableDirectory(id);
        var image = Path.Combine(folder, "image.png");
        if (IsEditable(id))
        {
            try { return Path.Combine(RevisionDirectory(folder, ReadRevision(folder)), "image.png"); }
            catch (Exception error) when (IsStorageError(error)) { return image; }
        }
        return Path.Combine(directory, id.ToString("N") + ".png");
    }
    internal bool Contains(Guid id) => id != Guid.Empty && File.Exists(PathFor(id));
    internal bool IsEditable(Guid id) => id != Guid.Empty && File.Exists(Path.Combine(EditableDirectory(id), "source.png"));

    internal ScreenshotEntry[] List()
    {
        if (!Directory.Exists(directory)) return [];
        var entries = new List<ScreenshotEntry>();
        foreach (var image in new DirectoryInfo(directory).EnumerateFiles("*.png"))
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(image.Name), "N", out var id) && id != Guid.Empty)
                entries.Add(new(id, image.CreationTimeUtc));
        }
        foreach (var record in new DirectoryInfo(directory).EnumerateDirectories("*.pin"))
        {
            var image = new FileInfo(Path.Combine(record.FullName, "image.png"));
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(record.Name), "N", out var id) && id != Guid.Empty &&
                (record.Attributes & FileAttributes.ReparsePoint) == 0 && image.Exists)
                entries.Add(new(id, image.CreationTimeUtc));
        }
        return entries.OrderByDescending(entry => entry.CreatedUtc).ToArray();
    }

    internal Guid ImportEditable(string sessionDirectory, (uint Width, uint Height) size)
    {
        var id = Guid.NewGuid();
        var destination = EditableDirectory(id);
        WritePinAssets(sessionDirectory, destination, size, includeSource: true);
        return id;
    }

    internal void PrepareEdit(Guid id, string sessionDirectory)
    {
        var folder = EditableDirectory(id);
        var current = RevisionDirectory(folder, ReadRevision(folder));
        using var source = OpenSessionFile(folder, "source.png", MaxFileBytes);
        using var document = OpenSessionFile(current, "document.json", MaxDocumentBytes);
        WriteAsset(source, Path.Combine(sessionDirectory, "edit-source.png"));
        WriteAsset(document, Path.Combine(sessionDirectory, "edit-document.json"));
    }

    internal byte[] ReadOcrImage(Guid id)
    {
        using var input = OpenRenderedImage(id);
        var image = new byte[checked((int)input.Length)];
        input.ReadExactly(image);
        return image;
    }

    internal void Export(Guid id, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        var parent = StorageLocation.ResolvePath(Path.GetDirectoryName(destination)!);
        var name = Path.GetFileName(destination);
        if (name.Contains(':') || !Path.GetExtension(name).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            StorageLocation.Within(parent, StorageLocation.ResolvePath(directory)))
            throw new InvalidDataException("Invalid screenshot export destination.");

        using var input = OpenRenderedImage(id);
        ReadSize(input);
        var temporary = Path.Combine(parent, ".pane-screenshot-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteAsset(input, temporary);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(parent, name), overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    FileStream OpenRenderedImage(Guid id)
    {
        var path = PathFor(id);
        var folder = Path.GetDirectoryName(path)!;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (IsEditable(id) && (File.GetAttributes(EditableDirectory(id)) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Invalid screenshot asset directory.");
        return OpenSessionFile(folder, Path.GetFileName(path), MaxFileBytes);
    }

    internal ScreenshotEdit StageEdit(Guid id, string sessionDirectory, (uint Width, uint Height) size)
    {
        var folder = EditableDirectory(id);
        var previous = ReadRevision(folder);
        using var source = OpenSessionFile(folder, "source.png", MaxFileBytes);
        if (ReadSize(source) != size) throw new InvalidDataException("An edit cannot change the original image dimensions.");
        var revision = Guid.NewGuid();
        WritePinAssets(sessionDirectory, RevisionDirectory(folder, revision), size, includeSource: false);
        return new(id, revision, previous);
    }

    internal void PublishEdit(ScreenshotEdit edit) => WriteRevision(EditableDirectory(edit.Id), edit.Revision);

    internal void FinishEdit(ScreenshotEdit edit, bool accepted)
    {
        var folder = EditableDirectory(edit.Id);
        try
        {
            if (!accepted && ReadRevision(folder) == edit.Revision) WriteRevision(folder, edit.Previous);
            var obsolete = accepted ? edit.Previous : edit.Revision;
            if (obsolete != null) RemoveEditableDirectory(RevisionDirectory(folder, obsolete));
        }
        catch (Exception error) when (IsStorageError(error)) { }
    }

    internal static Guid? ReadRevision(string folder)
    {
        if (!File.Exists(Path.Combine(folder, "current"))) return null;
        using var stream = OpenSessionFile(folder, "current", 32, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream);
        if (!Guid.TryParseExact(reader.ReadToEnd(), "N", out var revision) || revision == Guid.Empty ||
            !File.Exists(Path.Combine(RevisionDirectory(folder, revision), "image.png")) ||
            !File.Exists(Path.Combine(RevisionDirectory(folder, revision), "document.json")))
            throw new InvalidDataException("Invalid screenshot revision.");
        return revision;
    }

    static void WriteRevision(string folder, Guid? revision)
    {
        var destination = Path.Combine(folder, "current");
        if (revision == null) { File.Delete(destination); return; }
        var temporary = destination + ".tmp";
        using var input = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(revision.Value.ToString("N")));
        try
        {
            WriteAsset(input, temporary);
            File.Move(temporary, destination, true);
        }
        finally { File.Delete(temporary); }
    }

    static void WritePinAssets(string sessionDirectory, string destination, (uint Width, uint Height) size, bool includeSource)
    {
        using var image = OpenSessionFile(sessionDirectory, "pin.png", MaxFileBytes);
        using var source = OpenSessionFile(sessionDirectory, "pin-source.png", MaxFileBytes);
        if (ReadSize(image) != size || ReadSize(source) != size)
            throw new InvalidDataException("Native pin image dimensions do not match the request.");
        using var document = OpenSessionFile(sessionDirectory, "pin-document.json", MaxDocumentBytes);
        ValidateDocument(document, size);

        var temporary = destination + ".tmp";
        Directory.CreateDirectory(temporary);
        try
        {
            WriteAsset(image, Path.Combine(temporary, "image.png"));
            if (includeSource) WriteAsset(source, Path.Combine(temporary, "source.png"));
            WriteAsset(document, Path.Combine(temporary, "document.json"));
            Directory.Move(temporary, destination);
        }
        finally { RemoveEditableDirectory(temporary); }
    }

    internal void DiscardEditable(Guid id) => RemoveEditableDirectory(EditableDirectory(id));

    internal static void ValidateDocument(Stream document, (uint Width, uint Height) size)
    {
        using var parsed = JsonDocument.Parse(document);
        var data = parsed.RootElement;
        if (data.GetProperty("format").GetString() != "spark233-pin" || data.GetProperty("version").GetInt32() != 1 ||
            data.GetProperty("width").GetUInt32() != size.Width || data.GetProperty("height").GetUInt32() != size.Height ||
            data.GetProperty("shapes").GetArrayLength() > 2048)
            throw new InvalidDataException("Invalid native pin annotation document.");
    }

    internal void Delete(Guid id)
    {
        lock (gate) DeleteCore(id);
    }

    internal bool TrimUnpinnedHistory(Guid added, Func<Guid, bool> isPinned)
    {
        lock (gate)
        {
            var remainingSlots = UnpinnedHistoryLimit - (isPinned(added) ? 0 : 1);
            var obsolete = List().Where(entry => entry.Id != added && !isPinned(entry.Id)).Select(entry => entry.Id)
                .Distinct().Skip(remainingSlots).Reverse();
            var succeeded = true;
            foreach (var id in obsolete)
            {
                try { DeleteCore(id); }
                catch (Exception error) when (IsStorageError(error)) { succeeded = false; }
            }
            return succeeded;
        }
    }

    void DeleteCore(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Missing screenshot ID.", nameof(id));
        var root = new DirectoryInfo(directory);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid screenshot asset directory.");
        var folder = new DirectoryInfo(EditableDirectory(id));
        var plain = new FileInfo(Path.Combine(directory, id.ToString("N") + ".png"));
        if (!folder.Exists || plain.Exists && !IsEditable(id) && !File.Exists(Path.Combine(folder.FullName, "image.png")))
        {
            if (plain.Exists && (plain.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Invalid screenshot file.");
            File.Delete(plain.FullName);
            return;
        }

        var revisions = ReadEditableRevisions(folder);
        var staged = Path.Combine(root.FullName, id.ToString("N") + DeletedSuffix);
        Directory.Move(folder.FullName, staged);
        foreach (var revision in revisions) RemoveEditableDirectory(Path.Combine(staged, revision));
        File.Delete(Path.Combine(staged, "current"));
        File.Delete(Path.Combine(staged, "current.tmp"));
        RemoveEditableDirectory(staged);
    }

    static List<string> ReadEditableRevisions(DirectoryInfo folder)
    {
        if ((folder.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid editable screenshot directory.");
        var revisions = new List<string>();
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
                throw new InvalidDataException("Invalid screenshot asset.");
            if (entry is FileInfo && entry.Name is "image.png" or "source.png" or "document.json" or "current" or "current.tmp") continue;
            if (entry is not DirectoryInfo revision ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(revision.Name), "N", out _) ||
                Path.GetExtension(revision.Name) is not ("" or ".tmp") ||
                revision.EnumerateFileSystemInfos().Any(asset => asset is not FileInfo ||
                    (asset.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0 || asset.Name is not ("image.png" or "document.json")))
                throw new InvalidDataException("Unexpected screenshot revision assets.");
            revisions.Add(revision.Name);
        }
        return revisions;
    }

    static void RemoveEditableDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid editable screenshot directory.");
        File.Delete(Path.Combine(path, "image.png"));
        File.Delete(Path.Combine(path, "source.png"));
        File.Delete(Path.Combine(path, "document.json"));
        Directory.Delete(path);
    }

    internal static FileStream OpenSessionFile(string folder, string name, long maxBytes, FileShare share = FileShare.Read)
    {
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid screenshot asset directory.");
        var file = new FileInfo(Path.Combine(folder, name));
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid native screenshot file.");
        var input = file.Open(FileMode.Open, FileAccess.Read, share);
        if (input.Length is >= 2 && input.Length <= maxBytes) return input;
        input.Dispose();
        throw new InvalidDataException("Native screenshot file size is out of range.");
    }

    static void WriteAsset(Stream input, string path)
    {
        input.Position = 0;
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(true);
    }

    internal Guid Import(string source, (uint Width, uint Height)? expected = null)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var size = ReadSize(input);
        if (expected is { } declared && declared != size) throw new InvalidDataException("Screenshot dimensions do not match the capture result.");
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid();
        var destination = PathFor(id);
        var temporary = destination + ".tmp";
        try
        {
            WriteAsset(input, temporary);
            lock (gate) File.Move(temporary, destination);
            return id;
        }
        finally { File.Delete(temporary); }
    }

    internal static (uint Width, uint Height) ReadSize(Stream input)
    {
        if (input.Length is < 33 or > MaxFileBytes) throw new InvalidDataException("Screenshot file size is out of range.");
        Span<byte> header = stackalloc byte[24];
        input.ReadExactly(header);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!header[..8].SequenceEqual(signature) || BinaryPrimitives.ReadUInt32BigEndian(header[8..]) != 13 ||
            !header.Slice(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("Screenshot is not PNG.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension || (ulong)width * height > MaxPixels)
            throw new InvalidDataException("Screenshot pixel count is out of range.");
        return (width, height);
    }

    internal static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException or InvalidDataException;
}
