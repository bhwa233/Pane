using System.Drawing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Notes;
using Pane.Settings;
using Pane.Views;
using RectangleF = System.Drawing.RectangleF;

namespace Pane;

/// <summary>折叠便签共用一条贴边队列；主条移动整队，便签条只改变顺序。</summary>
public sealed partial class App
{
    readonly Dictionary<string, GlassPanel> capsuleQueues = [];
    GlassPanel? capsulePreviewPanel;
    NoteCapsulePreviewView? capsulePreviewView;
    DispatcherQueueTimer? capsuleHoverTimer;
    DispatcherQueueTimer? capsuleScrollCommit;
    DispatcherQueueTimer? capsuleFullscreenWatch;
    Guid? capsulePreviewNote, capsuleHoverNote, capsuleHighlightNote;
    bool capsulePreviewHovered;
    DispatcherQueueTimer? capsuleHideTimer;
    (string QueueKey, double Scroll)? pendingScrollCommit;
    string? capsuleDemotedMonitor;

    // ---------------- 状态读取 ----------------

    NoteCapsuleLayout? CapsuleState(Guid id) => Store.Current.NoteCapsules?.GetValueOrDefault(id);

    void SetCapsuleState(Guid id, NoteCapsuleLayout layout) => Store.Update(settings =>
    {
        var capsules = new Dictionary<Guid, NoteCapsuleLayout>(settings.NoteCapsules ?? []) { [id] = layout };
        return settings with { NoteCapsules = capsules };
    });

    /// <summary>队列成员：贴到桌面且有落点、未隐藏的便签（notes-mvp §6「展开入口保留」）。</summary>
    List<(Guid Id, NoteCapsuleSlot Slot)> QueueMembers(string monitorId, NoteEdge edge)
    {
        var settings = Store.Current;
        var pinned = settings.DesktopNotes ?? [];
        return settings.NoteCapsules?.Where(pair => pair.Value is { Hidden: false, Slot: { } slot } &&
                slot.Edge == (int)edge && slot.MonitorId == monitorId && pinned.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Value.Slot!.Order)
            .Select(pair => (pair.Key, pair.Value.Slot!)).ToList() ?? [];
    }

    static (string MonitorId, NoteEdge Edge) SplitQueueKey(string key)
    {
        var separator = key.LastIndexOf('|');
        return (key[..separator], (NoteEdge)int.Parse(key[(separator + 1)..]));
    }

    static RectangleF WorkDip((string Id, Native.Rect Work, float Dpi) monitor) =>
        new(0, 0, (monitor.Work.Right - monitor.Work.Left) / monitor.Dpi,
            (monitor.Work.Bottom - monitor.Work.Top) / monitor.Dpi);

    static Native.Point? MonitorPoint(string monitorId) => GlassPanel.MonitorById(monitorId) is { } monitor
        ? new Native.Point { X = monitor.Work.Left + 16, Y = monitor.Work.Top + 16 }
        : null;

    // ---------------- 刷新（布局 → 宿主） ----------------

    string? ActiveCapsuleKey(PaneSettings settings)
    {
        bool Available(string key)
        {
            var separator = key.LastIndexOf('|');
            return separator > 0 && int.TryParse(key[(separator + 1)..], out var edge) &&
                edge >= (int)NoteEdge.Left && edge <= (int)NoteEdge.Top &&
                GlassPanel.MonitorById(key[..separator]) != null;
        }
        return (settings.NoteCapsuleQueues ?? []).Keys.FirstOrDefault(Available) ??
            (settings.NoteCapsules ?? []).Values.Where(layout => layout.Slot != null)
                .Select(layout => NoteCapsuleGeometry.QueueKey(layout.Slot!.MonitorId, (NoteEdge)layout.Slot.Edge))
                .FirstOrDefault(Available);
    }

    void RefreshCapsules()
    {
        if (closing || !desktopNotesEnabled) return;
        // Quick-created desktop notes keep an entry even while their editor is open.
        foreach (var id in desktopNotes.Keys)
            if (CapsuleState(id) is { Folded: false, Hidden: false, Slot: null } layout)
                SetCapsuleState(id, layout with { Slot = FoldSlot(id) });
        var settings = Store.Current;
        var pinned = settings.DesktopNotes ?? [];
        var layouts = (settings.NoteCapsules ?? [])
            .Where(pair => pair.Value.Slot != null && pinned.ContainsKey(pair.Key)).ToArray();
        var key = ActiveCapsuleKey(settings);
        if (key == null)
        {
            // 旧版悬浮条没有贴边方向，迁移到原显示器顶部。
            var monitor = layouts.Select(pair => pair.Value.Slot!.MonitorId)
                .FirstOrDefault(id => GlassPanel.MonitorById(id) != null);
            if (monitor != null) key = NoteCapsuleGeometry.QueueKey(monitor, NoteEdge.Top);
        }
        foreach (var (oldKey, host) in capsuleQueues.ToArray())
            if (oldKey != key || !layouts.Any(pair => !pair.Value.Hidden)) DisposeQueueHost(oldKey, host);
        if (key == null || layouts.Length == 0) return;

        var (monitorId, edge) = SplitQueueKey(key);
        if (layouts.Any(pair => pair.Value.Slot is { } slot && (slot.MonitorId != monitorId || slot.Edge != (int)edge)) ||
            settings.NoteCapsuleQueues is not { Count: 1 } || !settings.NoteCapsuleQueues.ContainsKey(key))
            MergeCapsuleLayouts(key, key, settings.NoteCapsuleQueues?.GetValueOrDefault(key));

        var members = QueueMembers(monitorId, edge).Select(pair => CapsuleMember(pair.Id)).ToList();
        if (members.Count > 0) EnsureQueueHost(key, monitorId, edge, members);
    }

    void MergeCapsuleLayouts(string previous, string destination, NoteCapsuleQueue? queueState)
    {
        var (monitor, edge) = SplitQueueKey(destination);
        pendingScrollCommit = null;
        capsuleScrollCommit?.Stop();
        Store.Update(settings =>
        {
            var capsules = new Dictionary<Guid, NoteCapsuleLayout>(settings.NoteCapsules ?? []);
            var pinned = settings.DesktopNotes ?? [];
            var ordered = capsules.Where(pair => pair.Value.Slot != null && pinned.ContainsKey(pair.Key))
                .GroupBy(pair => NoteCapsuleGeometry.QueueKey(pair.Value.Slot!.MonitorId, (NoteEdge)pair.Value.Slot.Edge))
                .OrderBy(group => group.Key != previous)
                .SelectMany(group => group.OrderBy(pair => pair.Value.Slot!.Order)).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                var (id, layout) = ordered[i];
                capsules[id] = layout with { Slot = new(monitor, (int)edge, 0, 0, i) };
            }
            return settings with
            {
                NoteCapsules = capsules,
                NoteCapsuleQueues = new() { [destination] = queueState ?? new(DefaultStartFromKey(destination), false, 0) }
            };
        });
    }

    CapsuleMember CapsuleMember(Guid id)
    {
        var note = notes?.Find(id);
        var session = noteSessions?.Find(id);
        var title = (session?.Title ?? note?.Title ?? "").Trim();
        var due = GetNoteReminder(id);
        return new(id, title, due.HasValue, capsuleHighlightNote == id,
            Store.Current.DesktopNoteTopmost?.GetValueOrDefault(id) == true, IsNotePinned(id));
    }

    // ---------------- 队列宿主 ----------------

    void EnsureQueueHost(string key, string monitorId, NoteEdge edge, List<CapsuleMember> members)
    {
        if (capsuleQueues.TryGetValue(key, out var existing))
        {
            if (existing.View is NoteCapsuleQueueView view) view.Apply(QueueState(key, edge, members));
            RelayoutQueueHost(key, edge, members.Count);
            return;
        }
        var queueView = new NoteCapsuleQueueView();
        WireQueueView(key, queueView);
        var host = new GlassPanel(Strings.Notes_DesktopCard, queueView, queueView, Glass, desktopCard: true, contentOnly: true)
        {
            EscapeHides = false,
            HideOnBlur = () => false,
            DesktopWorkAreaChanged = () => queue.TryEnqueue(() =>
            {
                if (closing) return;
                // 屏幕变化：按新工作区夹紧后重摆（notes-mvp §7「重启与显示器变化」）。
                var (id, edge2) = SplitQueueKey(key);
                ClampQueueIntoWork(key, id, edge2);
                RefreshCapsules();
            }),
        };
        WatchFrames(host);
        host.HitRegion = () => queueView.BuildHitRegion(host.DpiScale, host.Width, host.Height);
        host.DesktopScreenPoint = MonitorPoint(monitorId) ?? default;
        capsuleQueues[key] = host;
        queueView.Apply(QueueState(key, edge, members));
        PlaceQueueHost(key, edge, members.Count);
        try { host.Show(activate: false); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            ReportFailure(Strings.Notes_DesktopCard, error);
            DisposeQueueHost(key, host);
            return;
        }
        if (capsuleDemotedMonitor == monitorId) host.PlacePanelInZOrder(1); // HWND_BOTTOM
        else host.SetTopmost(true);
        StartCapsuleFullscreenWatch();
    }

    CapsuleQueueState QueueState(string key, NoteEdge edge, List<CapsuleMember> members)
    {
        var (monitorId, _) = SplitQueueKey(key);
        var queue = Store.Current.NoteCapsuleQueues?.GetValueOrDefault(key);
        var start = queue?.Start ?? DefaultStartFor(monitorId, edge);
        return new(key, edge, queue?.Collapsed ?? false, start, queue?.Scroll ?? 0, members);
    }

    void RelayoutQueueHost(string key, NoteEdge edge, int members)
    {
        if (!capsuleQueues.TryGetValue(key, out var host) || !host.Shown) return;
        host.RequestSize(QueueHostSize(key, edge, members));
        host.DesktopAnchor = QueueHostAnchor(key, edge, members);
        GlassPanel.RestoreDesktopCards([host]);
    }

    void PlaceQueueHost(string key, NoteEdge edge, int members)
    {
        if (!capsuleQueues.TryGetValue(key, out var host)) return;
        host.RequestSize(QueueHostSize(key, edge, members));
        host.DesktopAnchor = QueueHostAnchor(key, edge, members);
    }

    float QueueHostLength(string key, NoteEdge edge, int members)
    {
        var (monitorId, _) = SplitQueueKey(key);
        var monitor = GlassPanel.MonitorById(monitorId);
        var work = monitor is { } area ? WorkDip(area) : new RectangleF(0, 0, 1920, 1040);
        var queue = QueueState(key, edge, []);
        return NoteCapsuleGeometry.HostLength(work, edge, queue.Start, members, members);
    }

    Windows.Foundation.Size QueueHostSize(string key, NoteEdge edge, int members)
    {
        var length = MathF.Max(QueueHostLength(key, edge, members), NoteCapsuleGeometry.MasterBaseWidth);
        return NoteCapsuleGeometry.IsVertical(edge)
            ? new(NoteCapsuleGeometry.CapsuleWidth, length)
            : new(length, NoteCapsuleGeometry.MasterHeight);
    }

    LauncherPosition QueueHostAnchor(string key, NoteEdge edge, int members)
    {
        var (monitorId, _) = SplitQueueKey(key);
        var monitor = GlassPanel.MonitorById(monitorId);
        var work = monitor is { } area ? WorkDip(area) : new RectangleF(0, 0, 1920, 1040);
        var queue = QueueState(key, edge, []);
        var host = NoteCapsuleGeometry.HostRect(work, edge, queue.Start, QueueHostLength(key, edge, members));
        return new(monitorId, host.X - work.X, host.Y - work.Y);
    }

    void DisposeQueueHost(string key, GlassPanel host)
    {
        capsuleQueues.Remove(key);
        if (host.View is NoteCapsuleQueueView view) view.OnHidden();
        (host.View as IDisposable)?.Dispose();
        host.Dispose();
    }


    void WireQueueView(string key, NoteCapsuleQueueView view)
    {
        view.ExpandRequested += id => ExpandNoteFromCapsule(id, temporaryTopmost: true);
        view.EditRequested += id => ExpandNoteFromCapsule(id, temporaryTopmost: true, focusBody: true);
        view.HideRequested += HideNote;
        view.DeleteRequested += DeleteNoteFromCapsule;
        view.MasterToggleRequested += () => ToggleQueue(key);
        view.DragMoved += result => UpdateCapsuleDrag(key, view, result);
        view.DragCancelled += EndCapsuleDrag;
        view.DragCompleted += result => queue.TryEnqueue(() => DragCompleted(key, result));
        view.ScrollDelta += delta => ScrollQueue(key, delta);
        view.HoverChanged += HoverCapsule;
        view.NewNoteRequested += QuickNewDesktopNote;
        view.OpenManagerRequested += () => ShowNotes();
        view.ShowAllRequested += ShowAllDesktopNotes;
    }

    void ExpandNoteFromCapsule(Guid id, bool temporaryTopmost = false, bool focusBody = false)
    {
        // 点击普通胶囊：打开或聚焦同 ID 的便签，恢复展开位置和尺寸，保留胶囊入口（notes-mvp §6）。
        if (CapsuleState(id) is not { } layout) return;
        capsuleHoverTimer?.Stop();
        HideCapsulePreview();
        if (layout is { Folded: true } or { Hidden: true })
            SetCapsuleState(id, layout with { Folded = false, Hidden = false });
        RefreshDesktopNotes();
        if (layout.Hidden) RefreshCapsules();
        FocusDesktopNote(id, focusBody, temporaryTopmost);
    }

    void HideNote(Guid id)
    {
        if (CapsuleState(id) is not { } layout) return;
        SetCapsuleState(id, layout with { Hidden = true });
        if (desktopNotes.Remove(id, out var removed))
        {
            removed.Dispose();
            RefreshDesktopCardsOverview();
        }
        RefreshCapsules();
    }

    void DeleteNoteFromCapsule(Guid id)
    {
        var hub = GetNoteSessions();
        if (hub.Find(id) is { } session && !hub.Flush(session)) return;
        hub.Forget(id);
        try
        {
            if (!GetNotes().Recycle(id)) return;
        }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { ReportFailure(Strings.Module_Notes_Name, error); return; }
        NoteRemoved(id);
        RefreshDesktopNotes();
        RefreshCapsules();
    }


    float DefaultStartFor(string monitorId, NoteEdge edge)
    {
        var monitor = GlassPanel.MonitorById(monitorId);
        var work = monitor is { } area ? WorkDip(area) : new RectangleF(0, 0, 1920, 1040);
        return NoteCapsuleGeometry.DefaultStart(work, edge);
    }

    void ReorderNote(string key, Guid id, int index)
    {
        var order = QueueMembersFromKey(key).Select(pair => pair.Id).ToList();
        if (!order.Remove(id)) return;
        order.Insert(Math.Clamp(index, 0, order.Count), id);
        Store.Update(settings =>
        {
            var capsules = new Dictionary<Guid, NoteCapsuleLayout>(settings.NoteCapsules ?? []);
            for (var position = 0; position < order.Count; position++)
                if (capsules.TryGetValue(order[position], out var layout) && layout.Slot is { } slot)
                    capsules[order[position]] = layout with { Slot = slot with { Order = position } };
            return settings with { NoteCapsules = capsules };
        });
        RefreshCapsules();
    }

    List<(Guid Id, NoteCapsuleSlot Slot)> QueueMembersFromKey(string key)
    {
        var (monitorId, edge) = SplitQueueKey(key);
        return QueueMembers(monitorId, edge);
    }

    void ToggleQueue(string key)
    {
        var queue = Store.Current.NoteCapsuleQueues?.GetValueOrDefault(key);
        var collapsed = !(queue?.Collapsed ?? false);
        Store.Update(settings =>
        {
            var queues = new Dictionary<string, NoteCapsuleQueue>(settings.NoteCapsuleQueues ?? [])
            {
                [key] = (queue ?? new(DefaultStartFromKey(key), false, 0)) with { Collapsed = collapsed },
            };
            return settings with { NoteCapsuleQueues = queues };
        });
        RefreshCapsules();
    }

    float DefaultStartFromKey(string key)
    {
        var (monitorId, edge) = SplitQueueKey(key);
        return DefaultStartFor(monitorId, edge);
    }

    void DragCompleted(string key, CapsuleDragResult result)
    {
        EndCapsuleDrag();
        var (monitorId, edge) = SplitQueueKey(key);
        if (!capsuleQueues.TryGetValue(key, out var host)) return;
        if (result.Master)
        {
            if (CapsuleDropTarget(key, result) is not { } target) return;
            var current = QueueState(key, edge, []);
            MergeCapsuleLayouts(key, NoteCapsuleGeometry.QueueKey(target.Monitor.Id, target.Edge),
                new(target.Start, current.Collapsed, target.Edge == edge ? current.Scroll : 0));
            RefreshCapsules();
            return;
        }

        if (GlassPanel.MonitorById(monitorId) is not { } monitor) return;
        var origin = host.Origin;
        var localX = (result.ScreenX - origin.X) / monitor.Dpi;
        var localY = (result.ScreenY - origin.Y) / monitor.Dpi;
        if (localX < 0 || localY < 0 || localX >= host.Width || localY >= host.Height) return;
        var members = QueueMembersFromKey(key);
        var oldIndex = members.FindIndex(member => member.Id == result.Id);
        if (oldIndex < 0) return;
        var delta = NoteCapsuleGeometry.IsVertical(edge) ? result.LocalY : result.LocalX;
        var index = Math.Clamp(oldIndex + (int)Math.Round(delta / NoteCapsuleGeometry.Step(edge),
            MidpointRounding.AwayFromZero), 0, members.Count - 1);
        if (index != oldIndex) ReorderNote(key, result.Id, index);
    }

    ((string Id, Native.Rect Work, float Dpi) Monitor, NoteEdge Edge, float Start)? CapsuleDropTarget(string key, CapsuleDragResult result)
    {
        if (GlassPanel.MonitorAt((int)result.ScreenX, (int)result.ScreenY) is not { } monitor) return null;
        var work = WorkDip(monitor);
        var x = (float)((result.ScreenX - monitor.Work.Left) / monitor.Dpi);
        var y = (float)((result.ScreenY - monitor.Work.Top) / monitor.Dpi);
        if (!work.Contains(x, y)) return null;
        var edge = NoteCapsuleGeometry.NearestEdge(work, new PointF(x, y));
        var distance = edge switch { NoteEdge.Left => x, NoteEdge.Right => work.Right - x, _ => y };
        // 沿用旧胶囊的 72 DIP 吸附范围；其余区域松手取消换边。
        if (distance > 72) return null;
        var count = QueueMembersFromKey(key).Count;
        var along = NoteCapsuleGeometry.IsVertical(edge) ? y - NoteCapsuleGeometry.MasterHeight / 2 :
            x - NoteCapsuleGeometry.MasterWidth(count) / 2;
        return (monitor, edge, NoteCapsuleGeometry.ClampStart(work, edge, along, count, count));
    }

    GlassPanel? capsuleDragGhost;
    DispatcherQueueTimer? capsuleDragPoll;
    NoteCapsuleQueueView? capsuleDragSource;

    void UpdateCapsuleDrag(string key, NoteCapsuleQueueView source, CapsuleDragResult result)
    {
        if (closing) return;
        if (capsuleDragSource == null)
        {
            capsuleDragSource = source;
            HideCapsulePreview();
            capsuleHoverTimer?.Stop();
            if (capsuleDragPoll == null)
            {
                capsuleDragPoll = queue.CreateTimer();
                capsuleDragPoll.Interval = TimeSpan.FromMilliseconds(12);
                capsuleDragPoll.Tick += (_, _) =>
                {
                    if ((GetAsyncKeyStateForDrag(0x1B) & 0x8000) != 0)
                        capsuleDragSource?.CancelActiveDrag();
                };
            }
            capsuleDragPoll.Start();
        }
        if (!result.Master) return;
        if (CapsuleDropTarget(key, result) is not { } target)
        {
            if (capsuleDragGhost is { Shown: true } preview) preview.Hide();
            return;
        }

        var monitor = target.Monitor;
        var count = QueueMembersFromKey(key).Count;
        var rect = NoteCapsuleGeometry.MasterRect(WorkDip(monitor), target.Edge, target.Start, count);
        var x = monitor.Work.Left + (int)Math.Round(rect.X * monitor.Dpi);
        var y = monitor.Work.Top + (int)Math.Round(rect.Y * monitor.Dpi);
        try
        {
            if (capsuleDragGhost == null)
            {
                var view = new NoteCapsuleGhostView();
                view.Apply(count.ToString(), master: true);
                capsuleDragGhost = new GlassPanel(Strings.Notes_DesktopCard, view, view, Glass,
                    desktopCard: true, contentOnly: true) { EscapeHides = false, HideOnBlur = () => false };
                WatchFrames(capsuleDragGhost);
                capsuleDragGhost.RequestSize(new(rect.Width, rect.Height));
            }
            var ghost = capsuleDragGhost;
            var corners = ((NoteCapsuleGhostView)ghost.View).Corners = NoteCapsuleQueueView.EdgeCorners(target.Edge);
            ghost.HitRegion = () => NoteCapsuleQueueView.RoundedRegion(0, 0, (int)Math.Round(rect.Width * ghost.DpiScale),
                (int)Math.Round(rect.Height * ghost.DpiScale), (int)Math.Round(Math.Max(corners.BottomLeft, corners.BottomRight) * ghost.DpiScale), corners);
            ghost.DesktopScreenPoint = new Native.Point { X = (int)result.ScreenX, Y = (int)result.ScreenY };
            ghost.DesktopAnchor = new(monitor.Id, rect.X, rect.Y);
            if (!ghost.Shown || ghost.DesktopPlacement?.MonitorId != monitor.Id)
            {
                ghost.Show(activate: false);
                ghost.SetTopmost(true);
            }
            else ghost.MoveTo(x, y);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            capsuleDragGhost?.Dispose();
            capsuleDragGhost = null;
            source.CancelActiveDrag();
            ReportFailure(Strings.Notes_DesktopCard, error);
        }
    }

    void EndCapsuleDrag()
    {
        capsuleDragPoll?.Stop();
        capsuleDragSource = null;
        capsuleDragGhost?.Dispose();
        capsuleDragGhost = null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    static extern short GetAsyncKeyStateForDrag(int key);

    void ScrollQueue(string key, double delta)
    {
        var (monitorId, edge) = SplitQueueKey(key);
        var monitor = GlassPanel.MonitorById(monitorId);
        var work = monitor is { } area ? WorkDip(area) : new RectangleF(0, 0, 1920, 1040);
        var members = QueueMembers(monitorId, edge).Count;
        var queue = QueueState(key, edge, QueueMembersFromKey(key).Select(pair => CapsuleMember(pair.Id)).ToList());
        if (pendingScrollCommit is { } pending && pending.QueueKey == key) queue = queue with { Scroll = pending.Scroll };
        var scroll = NoteCapsuleGeometry.ClampScroll(work, edge, queue.Start, queue.Scroll + delta, members, members);
        if (capsuleQueues.TryGetValue(key, out var host) && host.View is NoteCapsuleQueueView view)
        {
            view.Apply(queue with { Scroll = scroll });
            var origin = host.Origin;
            host.MoveTo(origin.X, origin.Y);
        }
        // 滚动结束后提交，不逐帧写设置（notes-mvp §7）。
        pendingScrollCommit = (key, scroll);
        capsuleScrollCommit ??= CreateScrollCommitTimer();
        capsuleScrollCommit.Stop();
        capsuleScrollCommit.Start();
    }

    DispatcherQueueTimer CreateScrollCommitTimer()
    {
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(600);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (pendingScrollCommit is not { } pending) return;
            pendingScrollCommit = null;
            Store.Update(settings =>
            {
                var queues = new Dictionary<string, NoteCapsuleQueue>(settings.NoteCapsuleQueues ?? []);
                var (monitorId, edge) = SplitQueueKey(pending.QueueKey);
                var current = queues.GetValueOrDefault(pending.QueueKey) ?? new(DefaultStartFor(monitorId, edge), false, 0);
                queues[pending.QueueKey] = current with { Scroll = (float)pending.Scroll };
                return settings with { NoteCapsuleQueues = queues };
            });
        };
        return timer;
    }

    // ---------------- 折叠 / 展开 / 找回 ----------------

    /// <summary>纸片关闭按钮 / Ctrl+W：折叠成胶囊（notes-mvp §6）。先提交共享草稿，失败保留编辑窗。</summary>
    void FoldNote(Guid id)
    {
        if (!IsNotePinned(id)) return;
        var hub = GetNoteSessions();
        if (hub.Find(id) is { } session && !hub.Flush(session))
        {
            ReportNoteSaveFailure(id);
            return;
        }
        var layout = CapsuleState(id);
        if (GetNotes().Find(id) == null)
        {
            RemoveNotePin(id);
            return;
        }
        NoteCapsuleSlot slot = layout?.Slot ?? FoldSlot(id);
        SetCapsuleState(id, new(true, false, slot));
        if (desktopNotes.Remove(id, out var removed)) removed.Dispose();
        RefreshDesktopNotes();
        RefreshCapsules();
    }

    void ReportNoteSaveFailure(Guid id)
    {
        if (desktopNotes.TryGetValue(id, out var panel)) panel.View.ReportSaveFailed();
    }

    /// <summary>首次折叠落点：当前屏幕最近的左 / 右 / 顶部边缘，追加到队尾（notes-mvp §6）。</summary>
    NoteCapsuleSlot FoldSlot(Guid id)
    {
        if (ActiveCapsuleKey(Store.Current) is { } active)
        {
            var (monitorId, activeEdge) = SplitQueueKey(active);
            var lastOrder = (Store.Current.NoteCapsules ?? []).Values
                .Where(layout => layout.Slot is { } slot && slot.MonitorId == monitorId && slot.Edge == (int)activeEdge)
                .Select(layout => layout.Slot!.Order).DefaultIfEmpty(-1).Max();
            return new(monitorId, (int)activeEdge, 0, 0, lastOrder + 1);
        }
        var point = new Native.Point();
        if (desktopNotes.TryGetValue(id, out var panel))
        {
            var bounds = panel.Bounds;
            point = new Native.Point { X = bounds.X + bounds.Width / 2, Y = bounds.Y + 16 };
        }
        else if (Native.GetCursorPos(out var cursor)) point = cursor;
        var monitor = GlassPanel.MonitorAt(point.X, point.Y);
        var work = monitor is { } area ? WorkDip(area) : new RectangleF(0, 0, 1920, 1040);
        var local = monitor is { } display
            ? new PointF((point.X - display.Work.Left) / display.Dpi, (point.Y - display.Work.Top) / display.Dpi)
            : new PointF(point.X, point.Y);
        var edge = NoteCapsuleGeometry.NearestEdge(work, local);
        var order = QueueMembers(monitor?.Id ?? "", edge).Count;
        return new(monitor?.Id ?? "", (int)edge, 0, 0, order);
    }

    /// <summary>托盘「显示全部桌面便签」：展开所有已隐藏但仍贴到桌面的便签（notes-mvp §3）。</summary>
    void ShowAllDesktopNotes()
    {
        var settings = Store.Current;
        var pinned = settings.DesktopNotes ?? [];
        var hidden = (settings.NoteCapsules ?? []).Where(pair => pair.Value.Hidden && pinned.ContainsKey(pair.Key)).ToList();
        if (hidden.Count == 0) return;
        Store.Update(current =>
        {
            var capsules = new Dictionary<Guid, NoteCapsuleLayout>(current.NoteCapsules ?? []);
            foreach (var pair in hidden) capsules[pair.Key] = pair.Value with { Hidden = false };
            return current with { NoteCapsules = capsules };
        });
        RefreshDesktopNotes();
        RefreshCapsules();
    }

    /// <summary>快速新建（托盘 / 主胶囊）：落点是新桌面便签，光标在正文（notes-mvp §3）。</summary>
    void QuickNewDesktopNote()
    {
        var hub = GetNoteSessions();
        var session = hub.New();
        // 贴到桌面并进入编辑面；空便签关闭不留痕由会话规则保证。
        Store.Update(settings =>
        {
            var layout = new Dictionary<Guid, LauncherPosition?>(settings.DesktopNotes ?? []) { [session.Id] = null };
            var surfaces = new Dictionary<Guid, int>(settings.DesktopNoteSurfaces ?? []) { [session.Id] = 1 };
            return settings with { DesktopNotes = layout, DesktopNoteSurfaces = surfaces };
        });
        SetCapsuleState(session.Id, new(false, false, null));
        RefreshDesktopNotes();
        RefreshCapsules();
        FocusDesktopNote(session.Id, focusBody: true, temporaryTopmost: true);
    }

    void FocusDesktopNote(Guid id, bool focusBody = false, bool temporaryTopmost = false)
    {
        if (!desktopNotes.TryGetValue(id, out var panel)) return;
        if (!panel.Shown) panel.Show();
        if (temporaryTopmost)
        {
            panel.TemporarilyTopmost = true;
            ApplyDesktopNoteTopmost(id);
        }
        panel.Activate();
        if (focusBody) panel.View.FocusBody();
    }

    // ---------------- 悬停预览（含胶囊 ↔ 预览卡之间的走廊，notes-mvp §7） ----------------

    void HoverCapsule(Guid id, int? index)
    {
        capsuleHoverNote = index is null ? null : id;
        capsuleHoverTimer ??= CreateHoverTimer();
        capsuleHoverTimer.Stop();
        if (capsuleHoverNote is { } note)
        {
            // 再进入走廊即续命：取消待收回，重新起显示延迟。
            capsuleHideTimer?.Stop();
            capsulePreviewNote = note;
            capsuleHoverTimer.Start();
        }
        else ScheduleHideCapsulePreview();
    }

    /// <summary>离开胶囊不立即收回：胶囊与预览卡分属两个窗口，二者间的窄通道不在任何岛内，
    /// 宽限期覆盖跨窗移动；只有胶囊与预览都不再悬停时才真正关闭。</summary>
    void ScheduleHideCapsulePreview()
    {
        capsuleHideTimer ??= CreateHideTimer();
        capsuleHideTimer.Stop();
        capsuleHideTimer.Start();
    }

    DispatcherQueueTimer CreateHideTimer()
    {
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (capsuleHoverNote != null) return;
            if (capsulePreviewHovered) { ScheduleHideCapsulePreview(); return; }
            HideCapsulePreview();
        };
        return timer;
    }

    void PreviewHoverChanged(bool hovered)
    {
        capsulePreviewHovered = hovered;
        if (hovered) capsuleHideTimer?.Stop();
        else ScheduleHideCapsulePreview();
    }

    DispatcherQueueTimer CreateHoverTimer()
    {
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (capsulePreviewNote is { } id && capsuleHoverNote == id) ShowCapsulePreview(id);
        };
        return timer;
    }

    void ShowCapsulePreview(Guid id)
    {
        var note = notes?.Find(id);
        if (note == null) { HideCapsulePreview(); return; }
        var session = noteSessions?.Find(id);
        var title = (session?.Title ?? note.Title ?? "").Trim();
        var body = session?.Body ?? note.Body;
        if (capsulePreviewView == null)
        {
            capsulePreviewView = new NoteCapsulePreviewView();
            capsulePreviewView.HoverChanged += PreviewHoverChanged;
        }
        if (capsulePreviewPanel is { } host)
        {
            host.Dispose(releaseView: false);
            capsulePreviewHovered = false;
        }
        var view = capsulePreviewView;
        view.Apply(id, title, body);
        var host2 = new GlassPanel(Strings.Notes_DesktopCard, view, view, Glass, desktopCard: true, contentOnly: true) { EscapeHides = false };
        WatchFrames(host2);
        capsulePreviewPanel = host2;
        if (PreviewPlacement(id, host2) is not { } placement) { HideCapsulePreview(); return; }
        host2.DesktopScreenPoint = placement.Screen;
        host2.DesktopAnchor = placement.Anchor;
        host2.RequestSize(view.PreferredSize);
        try
        {
            host2.Show(activate: false);
            if (capsuleDemotedMonitor != null && host2.DesktopPlacement?.MonitorId == capsuleDemotedMonitor)
                host2.PlacePanelInZOrder(1);
            else host2.SetTopmost(true);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            HideCapsulePreview();
        }
    }

    /// <summary>预览定位：顶部向下展开、左右向屏幕内侧展开，紧贴胶囊（Gap 即窄通道）。</summary>
    (Native.Point Screen, LauncherPosition Anchor)? PreviewPlacement(Guid id, GlassPanel previewHost)
    {
        foreach (var (key, host) in capsuleQueues)
        {
            if (host.View is not NoteCapsuleQueueView view || view.MemberOffset(id) is not { } offset) continue;
            var (monitorId, edge) = SplitQueueKey(key);
            var monitor = GlassPanel.MonitorById(monitorId);
            if (monitor is not { } area) continue;
            var work = WorkDip(area);
            var origin = host.Origin;
            var capsule = new RectangleF(
                work.X + (float)((origin.X - area.Work.Left) / area.Dpi) + (float)offset.X,
                work.Y + (float)((origin.Y - area.Work.Top) / area.Dpi) + (float)offset.Y,
                NoteCapsuleGeometry.CapsuleWidth, NoteCapsuleGeometry.CapsuleHeight);
            var size = capsulePreviewView!.PreferredSize;
            var rect = NoteCapsuleGeometry.PreviewRect(work, edge, capsule, new((float)size.Width, (float)size.Height));
            return (new Native.Point { X = (int)(area.Work.Left + rect.X * area.Dpi), Y = (int)(area.Work.Top + rect.Y * area.Dpi) },
                new(monitorId, rect.X, rect.Y));
        }
        return null;
    }

    void HideCapsulePreview()
    {
        capsuleHideTimer?.Stop();
        capsulePreviewHovered = false;
        if (capsulePreviewPanel is { } host)
        {
            (host.View as IDisposable)?.Dispose();
            host.Dispose();
            capsulePreviewPanel = null;
            capsulePreviewView = null;
        }
        capsulePreviewNote = null;
    }

    // ---------------- 提醒角标与到点高亮（notes-mvp §5） ----------------

    void HighlightCapsuleReminder(Guid id)
    {
        capsuleHighlightNote = id;
        RefreshCapsules();
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(20);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (capsuleHighlightNote == id) { capsuleHighlightNote = null; RefreshCapsules(); }
        };
        timer.Start();
    }

    // ---------------- 全屏避让（notes-mvp §7） ----------------

    void StartCapsuleFullscreenWatch()
    {
        if (capsuleFullscreenWatch != null || closing) return;
        capsuleFullscreenWatch = queue.CreateTimer();
        capsuleFullscreenWatch.Interval = TimeSpan.FromSeconds(2);
        capsuleFullscreenWatch.Tick += (_, _) => AvoidFullscreenWindows();
        capsuleFullscreenWatch.Start();
    }

    void StopCapsuleFullscreenWatch()
    {
        capsuleFullscreenWatch?.Stop();
        capsuleFullscreenWatch = null;
        SetCapsuleDemotedMonitor(null);
    }

    void AvoidFullscreenWindows()
    {
        if (!closing) SetCapsuleDemotedMonitor(ForegroundFullscreenMonitor());
    }

    static string? ForegroundFullscreenMonitor()
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == 0 || foreground == Native.GetShellWindow() || OwnWindow(foreground)) return null;
        var name = new System.Text.StringBuilder(256);
        Pane.Services.SelectionNative.GetClassName(foreground, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW") return null;

        const int windowStyle = -16, caption = 0x00C00000, thickFrame = 0x00040000;
        var style = GlassPanel.GetWindowLongPtrW(foreground, windowStyle);
        // 普通最大化在任务栏自动隐藏时也可能盖满屏幕。无框全屏仍按屏幕边界判断。
        if (GlassPanel.IsZoomed(foreground) && ((style & caption) == caption || (style & thickFrame) != 0)) return null;
        if (!Native.GetWindowRect(foreground, out var bounds)) return null;
        var monitor = GlassPanel.MonitorFromWindow(foreground, 2);
        var info = new GlassPanel.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<GlassPanel.MonitorInfo>() };
        if (!GlassPanel.GetMonitorInfoW(monitor, ref info) || bounds.Left > info.Monitor.Left || bounds.Top > info.Monitor.Top ||
            bounds.Right < info.Monitor.Right || bounds.Bottom < info.Monitor.Bottom) return null;
        return GlassPanel.MonitorAt(info.Monitor.Left, info.Monitor.Top)?.Id;
    }

    void SetCapsuleDemotedMonitor(string? monitorId)
    {
        if (capsuleDemotedMonitor == monitorId) return;
        var previous = capsuleDemotedMonitor;
        capsuleDemotedMonitor = monitorId;
        if (previous != null)
            foreach (var host in CapsulePanelsOn(previous)) host.SetTopmost(true);
        if (monitorId != null)
            foreach (var host in CapsulePanelsOn(monitorId)) host.PlacePanelInZOrder(1);
        foreach (var id in desktopNotes.Keys) ApplyDesktopNoteTopmost(id);
    }

    IEnumerable<GlassPanel> CapsulePanelsOn(string monitorId)
    {
        foreach (var edge in new[] { NoteEdge.Left, NoteEdge.Right, NoteEdge.Top })
            if (capsuleQueues.TryGetValue(NoteCapsuleGeometry.QueueKey(monitorId, edge), out var host))
                yield return host;
        if (capsulePreviewPanel is { Shown: true } preview && preview.DesktopPlacement?.MonitorId == monitorId)
            yield return preview;
    }

    static bool OwnWindow(nint window)
    {
        GetWindowThreadIdForProcess(window, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    static extern uint GetWindowThreadIdForProcess(nint window, out uint pid);

    // ---------------- 屏幕变化 ----------------

    /// <summary>「桌面便签」键盘弹层（notes-mvp §7）：列出所有贴到桌面的便签（含已隐藏），
    /// 方向键选择、回车展开、Esc 关闭。从启动器命令或托盘呼出；未核对的默认键位不猜。</summary>
    void ShowDesktopNotesPanel()
    {
        if (closing || !desktopNotesEnabled) return;
        var settings = Store.Current;
        var pinned = settings.DesktopNotes ?? [];
        var entries = pinned.Keys
            .Select(id => notes?.Find(id) is { } note
                ? new DesktopNoteEntry(id, note.Title.Trim(), IsNoteHidden(id), CapsuleState(id) is { Folded: true }, GetNoteReminder(id).HasValue)
                : null)
            .OfType<DesktopNoteEntry>().ToList();
        if (entries.Count == 0) return;
        GlassPanel? host = null;
        var view = new DesktopNotesPanelView(entries,
            id => { host?.Hide(); ExpandNoteFromCapsule(id); },
            () => host?.Hide());
        host = new GlassPanel(Strings.Notes_DesktopNotesPanel, view, view, Glass);
        WatchFrames(host);
        host.Hidden += (_, _) => queue.TryEnqueue(() =>
        {
            (host.View as IDisposable)?.Dispose();
            host.Dispose();
        });
        try { host.Show(activate: true); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            (host.View as IDisposable)?.Dispose();
            host.Dispose();
        }
    }

    void ClearCapsuleHosts()
    {
        EndCapsuleDrag();
        HideCapsulePreview();
        foreach (var (key, host) in capsuleQueues.ToArray()) DisposeQueueHost(key, host);
        capsuleHoverTimer?.Stop();
        capsuleHideTimer?.Stop();
        StopCapsuleFullscreenWatch();
    }

    void ClampQueueIntoWork(string key, string monitorId, NoteEdge edge)
    {
        var monitor = GlassPanel.MonitorById(monitorId);
        if (monitor is not { } area) return;
        var work = WorkDip(area);
        var members = QueueMembers(monitorId, edge).Count;
        var queue = Store.Current.NoteCapsuleQueues?.GetValueOrDefault(key);
        if (queue == null) return;
        var start = NoteCapsuleGeometry.ClampStart(work, edge, queue.Start, members, members);
        var scroll = NoteCapsuleGeometry.ClampScroll(work, edge, start, queue.Scroll, members, members);
        if (Math.Abs(start - queue.Start) < 0.01 && Math.Abs(scroll - queue.Scroll) < 0.01) return;
        Store.Update(settings =>
        {
            var queues = new Dictionary<string, NoteCapsuleQueue>(settings.NoteCapsuleQueues ?? []) { [key] = queue with { Start = start, Scroll = scroll } };
            return settings with { NoteCapsuleQueues = queues };
        });
    }
}
