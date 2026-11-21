namespace Pane.Settings;

/// <summary>整条便签提醒（notes-mvp §5）：按便签 ID 持久化、单计时器调度。
/// 旧记录里的 NoteTaskReminders 不再调度、不再弹窗；删除便签时顺手清掉遗留条目。</summary>
internal readonly record struct NoteReminder(Guid NoteId, DateTimeOffset Due);

internal static class NoteReminderSchedule
{
    internal static NoteReminder? Next(PaneSettings settings)
    {
        NoteReminder? next = null;
        foreach (var (note, due) in settings.NoteReminders ?? [])
            if (next is null || due < next.Value.Due) next = new(note, due);
        return next;
    }

    internal static PaneSettings Set(PaneSettings settings, Guid note, DateTimeOffset? due)
    {
        if (due is null && settings.NoteReminders?.ContainsKey(note) != true) return settings;
        var reminders = new Dictionary<Guid, DateTimeOffset>(settings.NoteReminders ?? []);
        if (due is { } time) reminders[note] = time;
        else reminders.Remove(note);
        return settings with { NoteReminders = reminders.Count == 0 ? null : reminders };
    }

    internal static PaneSettings RemoveNote(PaneSettings settings, Guid note)
    {
        settings = Set(settings, note, null);
        if (settings.NoteTaskReminders?.ContainsKey(note) != true) return settings;
        var byNote = new Dictionary<Guid, Dictionary<string, DateTimeOffset>>(settings.NoteTaskReminders);
        byNote.Remove(note);
        return settings with { NoteTaskReminders = byNote.Count == 0 ? null : byNote };
    }
}
