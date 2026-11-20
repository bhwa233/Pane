using Pane.Contracts;

namespace Pane.Services.Clipboard;

internal static class ClipboardCollections
{
    internal const int InputLimit = 256;
    const int NameLimit = 48;
    const int MembershipLimit = 12;
    internal static StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;

    internal static bool TryParse(string input, out IReadOnlyList<string>? collections)
    {
        collections = null;
        if (input.Length > InputLimit) return false;
        var names = input.Split([',', '\uFF0C'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(NameComparer).ToArray();
        if (!Valid(names)) return false;
        if (names.Length > 0) collections = Array.AsReadOnly(names);
        return true;
    }

    internal static bool Valid(IReadOnlyList<string>? names) => names is null ||
        names.Count <= MembershipLimit && names.Sum(name => name?.Length ?? 0) + Math.Max(names.Count - 1, 0) <= InputLimit &&
        names.All(name => name is { Length: > 0 and <= NameLimit } && name == name.Trim() && !name.Any(c => char.IsControl(c) || c is ',' or '\uFF0C')) &&
        names.Distinct(NameComparer).Count() == names.Count;

    internal static IReadOnlyList<string>? Combine(IReadOnlyList<string>? local, IReadOnlyList<string>? incoming)
    {
        if (incoming is not { Count: > 0 }) return local;
        if (local is not { Count: > 0 }) return incoming;
        var names = local.ToList();
        int length = names.Sum(name => name.Length) + names.Count - 1;
        foreach (var name in incoming)
        {
            if (names.Contains(name, NameComparer)) continue;
            if (names.Count >= MembershipLimit || length + name.Length + 1 > InputLimit)
                throw new InvalidDataException();
            names.Add(name);
            length += name.Length + 1;
        }
        return Array.AsReadOnly(names.ToArray());
    }

    internal static bool LeaveHistory(List<StoredClip> entries, Predicate<ClipEntry> matches)
    {
        bool changed = false;
        for (int index = entries.Count - 1; index >= 0; index--)
        {
            var clip = entries[index];
            if (clip.Entry.Archived || !matches(clip.Entry)) continue;
            if (clip.Entry.Collected) entries[index] = clip with { Entry = clip.Entry with { Archived = true, Pinned = false }, TextDraft = null };
            else entries.RemoveAt(index);
            changed = true;
        }
        return changed;
    }

    internal static bool TrimHistory(List<StoredClip> entries, int limit)
    {
        var protectedCount = entries.Count(clip => !clip.Entry.Archived && !clip.Entry.Pinned && clip.TextDraft != null);
        var remove = entries.Where(clip => !clip.Entry.Archived && !clip.Entry.Pinned && clip.TextDraft == null)
            .OrderByDescending(clip => clip.Entry.CopiedAt).Skip(Math.Max(0, limit - protectedCount)).Select(clip => clip.Entry.Id).ToHashSet();
        return remove.Count > 0 && LeaveHistory(entries, clip => remove.Contains(clip.Id));
    }
}
