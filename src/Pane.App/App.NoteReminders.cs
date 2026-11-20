using Microsoft.UI.Dispatching;
using Pane.Localization;
using Pane.Services.Notes;
using Pane.Settings;
using Pane.Views;

namespace Pane;

public sealed partial class App
{
    DispatcherQueueTimer? noteReminderTimer;

    void StartNoteReminders()
    {
        if (noteReminderTimer != null) return;
        noteReminderTimer = queue.CreateTimer();
        noteReminderTimer.IsRepeating = false;
        noteReminderTimer.Tick += NoteReminderTick;
        ScheduleNoteReminders();
    }

    void StopNoteReminders()
    {
        if (noteReminderTimer == null) return;
        noteReminderTimer.Stop();
        noteReminderTimer.Tick -= NoteReminderTick;
        noteReminderTimer = null;
        if (shell != null) shell.PowerTimeChanged -= ScheduleNoteReminders;
        RemoveSecondary(SecondaryId.NoteReminder);
    }

    DateTimeOffset? GetNoteReminder(Guid id) =>
        Store.Current.NoteReminders?.TryGetValue(id, out var due) == true ? due : null;

    IReadOnlyDictionary<string, DateTimeOffset>? GetTaskReminders(Guid id) =>
        Store.Current.NoteTaskReminders?.GetValueOrDefault(id);

    void SetNoteReminder(Guid id, string? task, DateTimeOffset? due)
    {
        Store.Update(settings => NoteReminderSchedule.Set(settings, id, task, due));
        RefreshNoteReminder(id);
        ScheduleNoteReminders();
    }

    void RemoveNoteReminders(Guid id)
    {
        Store.Update(settings => NoteReminderSchedule.RemoveNote(settings, id));
        RefreshNoteReminder(id);
        ScheduleNoteReminders();
    }

    void RefreshNoteReminder(Guid id)
    {
        if (secondaries.TryGetValue(SecondaryId.Notes, out var panel) && panel.View is NotesView view)
            view.RefreshReminder(id);
    }

    void ScheduleNoteReminders()
    {
        if (closing || noteReminderTimer is not { } timer) return;
        timer.Stop();
        if (shell != null) shell.PowerTimeChanged -= ScheduleNoteReminders;
        if (secondaries.ContainsKey(SecondaryId.NoteReminder) || NoteReminderSchedule.Next(Store.Current) is not { } next) return;
        if (shell != null) shell.PowerTimeChanged += ScheduleNoteReminders;
        var wait = next.Due - DateTimeOffset.UtcNow;
        timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds, 50, TimeSpan.FromDays(1).TotalMilliseconds));
        timer.Start();
    }

    void NoteReminderTick(DispatcherQueueTimer sender, object? args)
    {
        if (closing || sender != noteReminderTimer || secondaries.ContainsKey(SecondaryId.NoteReminder) ||
            NoteReminderSchedule.Next(Store.Current) is not { } next) return;
        if (next.Due > DateTimeOffset.UtcNow) { ScheduleNoteReminders(); return; }

        NoteDocument? note;
        try
        {
            var store = GetNotes();
            store.Load();
            note = store.Find(next.NoteId);
            if (note == null) RemoveNoteReminders(next.NoteId);
            else SetNoteReminder(next.NoteId, next.Task, null);
        }
        catch (Exception error) when (NoteStore.IsStorageError(error))
        {
            sender.Interval = TimeSpan.FromMinutes(1);
            sender.Start();
            return;
        }

        if (note == null) return;
        sender.Stop();
        var view = new NoteReminderView(note.Title, next.Task, () => ShowNotes(note.Id));
        ShowReminderPanel(SecondaryId.NoteReminder, Strings.Notes_Reminder, view, view, ScheduleNoteReminders);
    }
}
