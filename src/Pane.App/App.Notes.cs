using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Notes;
using Pane.Settings;
using Pane.Views;
using Windows.Foundation;
using Windows.Graphics;

namespace Pane;

/// <summary>删除快照（notes-mvp §4「删除可撤销」）：暂存到撤销期结束，撤销时恢复
/// 正文、分类、提醒，以及删除前的贴到桌面状态和位置。</summary>
internal sealed record NoteDeleteSnapshot(Guid Id, LauncherPosition? Position, int? Surface,
    double[]? Size, bool? Topmost, DateTimeOffset? Reminder);

public sealed partial class App
{
    NoteStore? notes;
    NoteSessions? noteSessions;
    readonly Dictionary<Guid, DesktopNoteWindow> desktopNotes = [];
    bool desktopNotesEnabled;
    bool temporaryNoteTopmost;
    Window? notesWindow;
    NotesView? notesView;
    RectInt32? notesWindowBounds;

    void ShowNotes(Guid? id = null)
    {
        if (closing || !AllowModule(ModuleIds.Notes)) return;
        foreach (var panel in panels.Values) panel.Hide();
        if (notesWindow == null)
        {
            var view = new NotesView(GetNotes(), GetNoteSessions(), IsNotePinned, ToggleNotePin,
                CaptureNoteForDelete, RecycleNoteForDelete, UndoNoteDelete, GetNoteReminder, SetNoteReminder,
                IsNoteHidden, RevealHiddenNote);
            var window = new Window
            {
                Title = Strings.Module_Notes_Name,
                Content = view,
                ExtendsContentIntoTitleBar = true
            };
            window.SetTitleBar(view.TitleDragRegion);
            var appWindow = window.AppWindow;
            var presenter = (OverlappedPresenter)appWindow.Presenter;
            presenter.IsResizable = true;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            AppIcon.Apply(hwnd);
            Native.GetCursorPos(out var cursor);
            var display = notesWindowBounds is { } saved
                ? DisplayArea.GetFromRect(saved, DisplayAreaFallback.Nearest)
                : DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
            var work = display.WorkArea;
            if (notesWindowBounds is { } bounds) appWindow.MoveAndResize(bounds);
            else
            {
                appWindow.Move(new(work.X, work.Y));
                var dpi = Native.GetDpiForWindow(hwnd) / 96.0;
                int width = (int)Math.Round(view.PreferredSize.Width * dpi);
                int height = (int)Math.Round(view.PreferredSize.Height * dpi);
                appWindow.MoveAndResize(new(work.X + (work.Width - width) / 2,
                    work.Y + (work.Height - height) / 4, width, height));
            }
            void UpdateWindowLayout()
            {
                var dpi = Native.GetDpiForWindow(hwnd) / 96.0;
                view.SetCaptionInsets(appWindow.TitleBar.LeftInset / dpi, appWindow.TitleBar.RightInset / dpi);
                presenter.PreferredMinimumWidth = (int)Math.Ceiling(view.MinimumSize.Width * dpi);
                presenter.PreferredMinimumHeight = (int)Math.Ceiling(view.MinimumSize.Height * dpi);
            }
            void ApplyTheme(object? sender, EventArgs args) =>
                view.RequestedTheme = Glass.Dark ? ElementTheme.Dark : ElementTheme.Light;
            ApplyTheme(null, EventArgs.Empty);
            Glass.Changed += ApplyTheme;
            window.SizeChanged += (_, _) => UpdateWindowLayout();
            UpdateWindowLayout();
            view.CloseRequested += (_, _) => window.Close();
            appWindow.Changed += (_, args) =>
            {
                if (presenter.State == OverlappedPresenterState.Restored && (args.DidPositionChange || args.DidSizeChange))
                    notesWindowBounds = new(appWindow.Position.X, appWindow.Position.Y, appWindow.Size.Width, appWindow.Size.Height);
            };
            window.Closed += (_, _) =>
            {
                Glass.Changed -= ApplyTheme;
                notesWindow = null;
                notesView = null;
                view.Dispose();
            };
            notesWindow = window;
            notesView = view;
            view.OnShowing(new(0, null, true));
        }
        if (id is { } noteId) notesView!.OpenNote(noteId);
        if (notesWindow.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } minimized)
            minimized.Restore();
        notesWindow.Activate();
    }

    bool IsNoteHidden(Guid id) => Store.Current.NoteCapsules?.GetValueOrDefault(id) is { Hidden: true };

    /// <summary>从便签列表 / 键盘弹层找回已隐藏的便签（notes-mvp §6「隐藏与找回」）。</summary>
    void RevealHiddenNote(Guid id)
    {
        if (!IsNoteHidden(id)) return;
        ExpandNoteFromCapsule(id);
    }

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
        noteSessions.Changed += (session, _) =>
        {
            foreach (var panel in capsuleQueues.Values)
                ((NoteCapsuleQueueView)panel.View).UpdateMemberTitle(session.Id, session.Title.Trim());
        };
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
        notesView?.RefreshSavedNotes();
        return true;
    }

    bool IsNotePinned(Guid id) => Store.Current.DesktopNotes?.ContainsKey(id) == true;

    DesktopNoteView CreateNoteCardView(Guid id)
    {
        var view = new DesktopNoteView(GetNoteSessions(), id, new DesktopNoteHost(this));
        view.UnpinRequested += (_, _) => RemoveNotePin(id);
        view.FoldRequested += (_, id) => FoldNote(id);
        return view;
    }

    sealed class DesktopNoteHost(App app) : IDesktopNoteHost
    {
        public void SaveSurface(Guid id, bool pad) => app.Store.Update(settings => settings with
            { DesktopNoteSurfaces = SetEntry(settings.DesktopNoteSurfaces, id, pad ? 1 : 0) });
        public bool GetSurface(Guid id) => app.Store.Current.DesktopNoteSurfaces?.GetValueOrDefault(id) == 1;
        public Size? GetSize(Guid id) => app.Store.Current.DesktopNoteSizes?.GetValueOrDefault(id) is { Length: 2 } pair
            && pair[0] >= DesktopNoteView.MinCardWidth && pair[1] >= DesktopNoteView.MinCardHeight ? new(pair[0], pair[1]) : null;
        public void ToggleTopmost(Guid id)
        {
            app.Store.Update(settings => settings with { DesktopNoteTopmost = SetEntry(settings.DesktopNoteTopmost, id, !IsTopmost(id)) });
            app.ApplyDesktopNoteTopmost(id);
            app.RefreshCapsules();
        }
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
            // 折叠或隐藏的便签不留正文窗口（notes-mvp §6）；胶囊由协调层承载。
            var capsule = Store.Current.NoteCapsules?.GetValueOrDefault(id);
            if (layout?.ContainsKey(id) == true && HasDesktopNote(id) &&
                capsule is not { Folded: true } and not { Hidden: true }) continue;
            if (desktopNotes.Remove(id, out var removed)) removed.Dispose();
        }
        if (layout != null)
        {
            foreach (var (id, position) in layout)
            {
                if (!HasDesktopNote(id)) continue;
                // 折叠 / 隐藏的便签只保留胶囊入口，不展开正文窗口（notes-mvp §6）。
                var capsule = Store.Current.NoteCapsules?.GetValueOrDefault(id);
                if (capsule is { Folded: true } or { Hidden: true }) continue;
                if (desktopNotes.ContainsKey(id)) continue;
                Native.GetCursorPos(out var screen);
                var card = new DesktopNoteWindow(CreateNoteCardView(id), Glass, position, screen, DesktopCardObstacles(),
                    () => SaveDesktopNoteBounds(id), () => FoldNote(id), QueueDesktopReflow, () => ApplyDesktopNoteTopmost(id));
                desktopNotes.Add(id, card);
                ApplyDesktopNoteTopmost(id);
                if (!desktopCardsHidden) card.Show();
                if (position == null && card.Position is { } placed)
                    HandleNoteLayout(() => SaveNoteLayout(id, true, placed));
            }
        }
        notesView?.RefreshPin();
        RefreshDesktopCardsOverview();

        bool HasDesktopNote(Guid id) => notes?.Find(id) != null || noteSessions?.Find(id) is { Persisted: false };
    }

    void ApplyDesktopNoteTopmost(Guid id)
    {
        if (desktopNotes.TryGetValue(id, out var card))
            card.SetTopmost((card.TemporarilyTopmost || temporaryNoteTopmost || Store.Current.DesktopNoteTopmost?.GetValueOrDefault(id) == true) &&
                (capsuleDemotedMonitors.Count == 0 || card.Monitor is not { } screen || !capsuleDemotedMonitors.Contains(screen.Id)));
    }

    void SaveDesktopNoteBounds(Guid id)
    {
        if (desktopArrangement != null || !desktopNotes.TryGetValue(id, out var card)) return;
        HandleNoteLayout(() => Store.Update(settings =>
        {
            var positions = new Dictionary<Guid, LauncherPosition?>(settings.DesktopNotes ?? []);
            var sizes = new Dictionary<Guid, double[]>(settings.DesktopNoteSizes ?? []);
            RecordDesktopNoteBounds(id, card, positions, sizes);
            return settings with { DesktopNotes = positions, DesktopNoteSizes = sizes };
        }));
    }

    static void RecordDesktopNoteBounds(Guid id, DesktopNoteWindow card, Dictionary<Guid, LauncherPosition?> positions,
        Dictionary<Guid, double[]> sizes)
    {
        positions[id] = card.Position;
        var size = card.Size;
        sizes[id] = [Math.Round(size.Width, 1), Math.Round(size.Height, 1)];
    }

    void HandleNoteLayout(Action operation)
    {
        try { operation(); }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { ReportFailure(Strings.Notes_DesktopCard,error); }
    }

    void RemoveNotePin(Guid id)
    {
        if (IsNotePinned(id)) HandleNoteLayout(() => { SaveNoteLayout(id, false, null); RefreshDesktopNotes(); });
        // 取消贴到桌面同时清掉胶囊入口和落点（notes-mvp §6「模块停用／删除」）。
        Store.Update(settings =>
        {
            if (settings.NoteCapsules?.ContainsKey(id) != true) return settings;
            var capsules = new Dictionary<Guid, NoteCapsuleLayout>(settings.NoteCapsules);
            capsules.Remove(id);
            return settings with { NoteCapsules = capsules.Count == 0 ? null : capsules };
        });
        RefreshCapsules();
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
        temporaryNoteTopmost = false;
        foreach (var card in desktopNotes.Values) card.Dispose();
        desktopNotes.Clear();
        ClearCapsuleHosts();
        RefreshDesktopCardsOverview();
    }

    sealed class NotesModule(App app) : Capability(app, ModuleIds.Notes, ModuleCategory.System, "pencil")
    {
        public override Task StartAsync(CancellationToken token)
        {
            App.desktopNotesEnabled = true;
            App.RefreshDesktopNotes();
            // 重启恢复：可见未折叠的便签开窗，折叠 / 隐藏的按队列状态收成胶囊（notes-mvp §6）。
            App.RefreshCapsules();
            App.StartCapsuleFullscreenWatch();
            App.StartNoteReminders();
            return Task.CompletedTask;
        }

        public override Task StopAsync(CancellationToken token)
        {
            // 停用前结清全部会话（notes-mvp §6「模块停用」）：释放窗口和事件并保留本机布局。
            App.GetNoteSessions().FlushAll();
            App.notesWindow?.Close();
            App.StopNoteReminders();
            App.StopCapsuleFullscreenWatch();
            App.ClearDesktopNotes();
            return Task.CompletedTask;
        }
    }
}
