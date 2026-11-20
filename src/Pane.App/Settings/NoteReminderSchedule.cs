namespace Pane.Settings;

internal readonly record struct NoteReminder(Guid NoteId, string? Task, DateTimeOffset Due);

internal static class NoteReminderSchedule
{
    internal const int TaskLabelLimit = 500;

    internal static NoteReminder? Next(PaneSettings settings)
    {
        NoteReminder? next = null;
        foreach (var (note, due) in settings.NoteReminders ?? [])
            if (next is null || due < next.Value.Due) next = new(note, null, due);
        foreach (var (note, tasks) in settings.NoteTaskReminders ?? [])
        {
            if (tasks is null) continue;
            foreach (var (task, due) in tasks)
                if (task.Length is > 0 and <= TaskLabelLimit && (next is null || due < next.Value.Due))
                    next = new(note, task, due);
        }
        return next;
    }

    internal static PaneSettings Set(PaneSettings settings, Guid note, string? task, DateTimeOffset? due)
    {
        if (task is null)
        {
            if (due is null && settings.NoteReminders?.ContainsKey(note) != true) return settings;
            var reminders = new Dictionary<Guid, DateTimeOffset>(settings.NoteReminders ?? []);
            if (due is { } time) reminders[note] = time;
            else reminders.Remove(note);
            return settings with { NoteReminders = reminders.Count == 0 ? null : reminders };
        }

        if (task.Length is 0 or > TaskLabelLimit) throw new ArgumentException(nameof(task));
        var existing = settings.NoteTaskReminders?.GetValueOrDefault(note);
        if (due is null && existing?.ContainsKey(task) != true) return settings;
        var tasks = new Dictionary<string, DateTimeOffset>(existing ?? [], StringComparer.Ordinal);
        if (due is { } taskTime) tasks[task] = taskTime;
        else tasks.Remove(task);
        var byNote = new Dictionary<Guid, Dictionary<string, DateTimeOffset>>(settings.NoteTaskReminders ?? []);
        if (tasks.Count == 0) byNote.Remove(note);
        else byNote[note] = tasks;
        return settings with { NoteTaskReminders = byNote.Count == 0 ? null : byNote };
    }

    internal static PaneSettings RemoveNote(PaneSettings settings, Guid note)
    {
        settings = Set(settings, note, null, null);
        if (settings.NoteTaskReminders?.ContainsKey(note) != true) return settings;
        var byNote = new Dictionary<Guid, Dictionary<string, DateTimeOffset>>(settings.NoteTaskReminders);
        byNote.Remove(note);
        return settings with { NoteTaskReminders = byNote.Count == 0 ? null : byNote };
    }
}
