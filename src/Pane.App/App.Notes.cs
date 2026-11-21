using Microsoft.UI.Dispatching;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Notes;
using Pane.Settings;
using Pane.Views;
using Windows.Foundation;

namespace Pane;

/// <summary>删除快照（notes-mvp §4「删除可撤销」）：暂存到撤销期结束，撤销时恢复
/// 正文、分类、提醒，以及删除前的贴到桌面状态和位置。</summary>
internal sealed record NoteDeleteSnapshot(Guid Id, LauncherPosition? Position, int? Surface,
    double[]? Size, bool? Topmost, DateTimeOffset? Reminder);

public sealed partial class App
{
    NoteStore? notes;
    NoteSessions? noteSessions;
    readonly Dictionary<Guid, GlassPanel> desktopNotes = [];
    bool desktopNotesEnabled;

    void ShowNotes(Guid? id = null) => Present(SecondaryId.Notes, Strings.Module_Notes_Name,
        () => new NotesView(GetNotes(), GetNoteSessions(), IsNotePinned, ToggleNotePin,
            CaptureNoteForDelete, RecycleNoteForDelete, UndoNoteDelete, GetNoteReminder, SetNoteReminder), view =>
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

    NoteSessions GetNoteSessions()
    {
        if (noteSessions != null) return noteSessions;
        noteSessions = new NoteSessions(GetNotes(), action => queue.TryEnqueue(action.Invoke), NewNoteTimer);
        return noteSessions;
    }

    NoteTimer NewNoteTimer() => new DispatcherNoteTimer(queue.CreateTimer());

    sealed class DispatcherNoteTimer(DispatcherQueueTimer timer) : NoteTimer
    {
        readonly Dictionary<EventHandler, TypedEventHandler<DispatcherQueueTimer, object?>> handlers = [];

        public TimeSpan Interval { get => timer.Interval; set => timer.Interval = value; }
        public bool IsRepeating { get => timer.IsRepeating; set => timer.IsRepeating = value; }

        public event EventHandler Tick
        {
            add
            {
                TypedEventHandler<DispatcherQueueTimer, object?> handler = (_, _) => value?.Invoke(timer, EventArgs.Empty);
                handlers[value] = handler;
                timer.Tick += handler;
            }
            remove
            {
                if (handlers.Remove(value, out var handler)) timer.Tick -= handler;
            }
        }

        public void Start() => timer.Start();
        public void Stop() => timer.Stop();
        public void Dispose()
        {
            timer.Stop();
            foreach (var handler in handlers.Values) timer.Tick -= handler;
            handlers.Clear();
        }
    }

    bool SaveToolResultAsNote(string title, string body)
    {
        if (closing || !Modules.IsActive(ModuleIds.Notes)) return false;
        try { GetNotes().Create(title, body); }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { return false; }
        if (secondaries.TryGetValue(SecondaryId.Notes, out var editor)) ((NotesView)editor.View).RefreshSavedNotes();
        return true;
    }

    bool IsNotePinned(Guid id) => Store.Current.DesktopNotes?.ContainsKey(id) == true;

    DesktopNoteView CreateNoteCardView(NoteDocument note)
    {
        var view = new DesktopNoteView(GetNoteSessions(), note, new DesktopNoteHost(this));
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
        public void SetReminder(Guid id, DateTimeOffset? due) => app.SetNoteReminder(id, due);
        public DateTimeOffset? GetReminder(Guid id) => app.GetNoteReminder(id);
        public void DeleteNote(Guid id)
        {
            // 先结清共享草稿；失败保留窗口（notes-mvp §2「失败不关窗」）。
            var hub = app.GetNoteSessions();
            if (hub.Find(id) is { } session && !hub.Flush(session)) return;
            hub.Forget(id);
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

    // ---------------- 删除可撤销（notes-mvp §4） ----------------

    NoteDeleteSnapshot? CaptureNoteForDelete(Guid id)
    {
        if (GetNotes().Find(id) == null) return null;
        var settings = Store.Current;
        return new NoteDeleteSnapshot(id,
            settings.DesktopNotes?.GetValueOrDefault(id),
            settings.DesktopNoteSurfaces?.TryGetValue(id, out var surface) == true ? surface : null,
            settings.DesktopNoteSizes?.TryGetValue(id, out var size) == true ? size : null,
            settings.DesktopNoteTopmost?.TryGetValue(id, out var topmost) == true ? topmost : null,
            GetNoteReminder(id));
    }

    void RecycleNoteForDelete(Guid id)
    {
        GetNoteSessions().Forget(id);
        try
        {
            if (!GetNotes().Recycle(id)) return;
        }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { ReportFailure(Strings.Module_Notes_Name, error); return; }
        NoteRemoved(id);
        RefreshDesktopNotes();
    }

    void UndoNoteDelete(NoteDeleteSnapshot snapshot)
    {
        try
        {
            if (!GetNotes().RestoreRecycled(snapshot.Id)) return;
        }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { ReportFailure(Strings.Module_Notes_Name, error); return; }
        Store.Update(settings =>
        {
            var next = settings;
            if (snapshot.Position is { } position)
            {
                var layout = new Dictionary<Guid, LauncherPosition?>(next.DesktopNotes ?? []) { [snapshot.Id] = position };
                next = next with { DesktopNotes = layout };
            }
            if (snapshot.Surface is { } surface)
                next = next with { DesktopNoteSurfaces = new(next.DesktopNoteSurfaces ?? []) { [snapshot.Id] = surface } };
            if (snapshot.Size is { } size)
                next = next with { DesktopNoteSizes = new(next.DesktopNoteSizes ?? []) { [snapshot.Id] = size } };
            if (snapshot.Topmost is { } topmost)
                next = next with { DesktopNoteTopmost = new(next.DesktopNoteTopmost ?? []) { [snapshot.Id] = topmost } };
            return next;
        });
        if (snapshot.Reminder is { } due) SetNoteReminder(snapshot.Id, due);
        RefreshDesktopNotes();
    }

    // ---------------- 桌面布局 ----------------

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
            if (desktopNotes.Remove(id, out var removed))
            {
                (removed.View as IDisposable)?.Dispose();
                removed.Dispose();
            }
        }
        if (layout != null)
        {
            foreach (var (id, position) in layout.Take(NoteStore.DesktopPinLimit))
            {
                if (notes?.Find(id) is not { } note) continue;
                if (desktopNotes.TryGetValue(id, out var existing)) continue;
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
        foreach (var card in desktopNotes.Values)
        {
            (card.View as IDisposable)?.Dispose();
            card.Dispose();
        }
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
            // 停用前结清全部会话（notes-mvp §6「模块停用」）：释放窗口和事件并保留本机布局。
            App.GetNoteSessions().FlushAll();
            App.RemoveSecondary(SecondaryId.Notes);
            App.StopNoteReminders();
            App.ClearDesktopNotes();
            return Task.CompletedTask;
        }
    }
}
