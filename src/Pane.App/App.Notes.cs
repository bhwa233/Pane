using Pane.Contracts;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Notes;
using Pane.Settings;
using Pane.Views;
using Windows.Foundation;

namespace Pane;

public sealed partial class App
{
    NoteStore? notes;
    readonly Dictionary<Guid, GlassPanel> desktopNotes = [];
    bool desktopNotesEnabled;

    void ShowNotes(Guid? id = null) => Present(SecondaryId.Notes, Strings.Module_Notes_Name,
        () => new NotesView(GetNotes(), IsNotePinned, ToggleNotePin, NoteRemoved, GetNoteReminder, GetTaskReminders, SetNoteReminder), view =>
        {
            if (id is { } noteId) ((NotesView)view).OpenNote(noteId);
        });

    NoteStore GetNotes()
    {
        if (notes != null) return notes;
        notes = new NoteStore(Path.Combine(Store.DirectoryPath, "notes"), Store.StorageSync);
        notes.Changed += (_, _) => queue.TryEnqueue(() => RefreshDesktopNotes());
        return notes;
    }

    bool SaveToolResultAsNote(string title, string body)
    {
        if (closing || !Modules.IsActive(ModuleIds.Notes)) return false;
        GetNotes().SaveNew(title, body);
        if (secondaries.TryGetValue(SecondaryId.Notes, out var editor)) ((NotesView)editor.View).RefreshSavedNotes();
        return true;
    }

    bool IsNotePinned(Guid id) => Store.Current.DesktopNotes?.ContainsKey(id) == true;

    DesktopNoteView CreateNoteCardView(NoteDocument note)
    {
        var view = new DesktopNoteView(note, GetNotes(), new DesktopNoteHost(this));
        view.UnpinRequested += (_, _) => RemoveNotePin(note.Id);
        return view;
    }

    sealed class DesktopNoteHost(App app) : IDesktopNoteHost
    {
        public void SaveSurface(Guid id, bool pad) => app.Store.Update(settings => settings with
            { DesktopNoteSurfaces = SetEntry(settings.DesktopNoteSurfaces, id, pad ? 1 : 0) });
        public bool GetSurface(Guid id) => app.Store.Current.DesktopNoteSurfaces?.GetValueOrDefault(id) == 1;
        public void SaveSize(Guid id, double width, double height) => app.Store.Update(settings => settings with
            { DesktopNoteSizes = SetEntry(settings.DesktopNoteSizes, id, [Math.Round(width, 1), Math.Round(height, 1)]) });
        public Size? GetSize(Guid id) => app.Store.Current.DesktopNoteSizes?.GetValueOrDefault(id) is { Length: 2 } pair
            && pair[0] >= DesktopNoteView.MinCardWidth && pair[1] >= DesktopNoteView.MinCardHeight ? new(pair[0], pair[1]) : null;
        public void ToggleTopmost(Guid id) => app.Store.Update(settings => settings with
            { DesktopNoteTopmost = SetEntry(settings.DesktopNoteTopmost, id, !(settings.DesktopNoteTopmost?.GetValueOrDefault(id) == true)) });
        public bool IsTopmost(Guid id) => app.Store.Current.DesktopNoteTopmost?.GetValueOrDefault(id) == true;
        public void SetReminder(Guid id, DateTimeOffset? due) => app.SetNoteReminder(id, null, due);
        public DateTimeOffset? GetReminder(Guid id) => app.GetNoteReminder(id);
        public void DeleteNote(Guid id)
        {
            try
            {
                if (!app.GetNotes().Recycle(id)) return;
            }
            catch (Exception error) when (NoteStore.IsStorageError(error)) { return; }
            app.NoteRemoved(id);
            app.RefreshDesktopNotes();
        }
        static Dictionary<Guid,TValue> SetEntry<TValue>(Dictionary<Guid,TValue>? map, Guid id, TValue value) =>
            new(map ?? []) { [id] = value };
    }

    void ToggleNotePin(Guid id)
    {
        var pinned = IsNotePinned(id);
        if (!pinned && (Store.Current.DesktopNotes?.Count ?? 0) >= NoteStore.DesktopPinLimit)
            throw new InvalidOperationException(Strings.Notes_PinLimit);
        SaveNoteLayout(id, !pinned, null);
        RefreshDesktopNotes();
    }

    void SaveNoteLayout(Guid id, bool pinned, LauncherPosition? position) => Store.Update(settings =>
    {
        var layout = new Dictionary<Guid,LauncherPosition?>(settings.DesktopNotes ?? []);
        if (pinned) layout[id] = position;
        else layout.Remove(id);
        return settings with { DesktopNotes = layout.Count == 0 ? null : layout };
    });

    void RefreshDesktopNotes()
    {
        if (closing || !desktopNotesEnabled) return;
        var layout = Store.Current.DesktopNotes;
        if (layout is { Count: > 0 })
        {
            try { GetNotes().Load(); }
            catch (Exception error) when (NoteStore.IsStorageError(error))
            {
                ReportFailure(Strings.Module_Notes_Name, error);
                return;
            }
        }
        foreach (var id in desktopNotes.Keys.ToArray())
        {
            if (layout?.ContainsKey(id) == true && notes?.Find(id) != null) continue;
            if (desktopNotes.Remove(id, out var removed)) removed.Dispose();
        }
        if (layout != null)
        {
            foreach (var (id, position) in layout.Take(8))
            {
                if (notes?.Find(id) is not { } note) continue;
                if (desktopNotes.TryGetValue(id, out var existing)) ((DesktopNoteView)existing.View).Apply(note);
                else
                {
                    var view = CreateNoteCardView(note);
                    if (CreateDesktopCard(Strings.Notes_DesktopCard, view, view, position,
                        anchor => SaveNoteLayout(id, true, anchor)) is { } panel)
                    {
                        // 形态 / 尺寸 / 置顶在卡挂到窗口之后应用（galaxy surface_state 的恢复路径）。
                        view.AttachPanel(panel);
                        desktopNotes.Add(id,panel);
                    }
                }
            }
        }
        if (secondaries.TryGetValue(SecondaryId.Notes,out var editor)) ((NotesView)editor.View).RefreshPin();
        RefreshDesktopCardsOverview();
    }

    void HandleNoteLayout(Action operation)
    {
        try { operation(); }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { ReportFailure(Strings.Notes_DesktopCard,error); }
    }

    void RemoveNotePin(Guid id)
    {
        if (IsNotePinned(id)) HandleNoteLayout(() => { SaveNoteLayout(id, false, null); RefreshDesktopNotes(); });
    }

    void NoteRemoved(Guid id)
    {
        RemoveNotePin(id);
        try { RemoveNoteReminders(id); }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { ReportFailure(Strings.Notes_Reminder, error); }
    }

    void ClearDesktopNotes()
    {
        desktopNotesEnabled = false;
        foreach (var card in desktopNotes.Values) card.Dispose();
        desktopNotes.Clear();
        RefreshDesktopCardsOverview();
    }

    sealed class NotesModule(App app) : Capability(app, ModuleIds.Notes, ModuleCategory.System, "pencil")
    {
        public override Task StartAsync(CancellationToken token)
        {
            App.desktopNotesEnabled = true;
            App.RefreshDesktopNotes();
            App.StartNoteReminders();
            return Task.CompletedTask;
        }

        public override Task StopAsync(CancellationToken token)
        {
            App.RemoveSecondary(SecondaryId.Notes);
            App.StopNoteReminders();
            App.ClearDesktopNotes();
            return Task.CompletedTask;
        }
    }
}
