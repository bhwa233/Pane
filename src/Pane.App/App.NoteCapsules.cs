using System.Drawing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Notes;
using Pane.Settings;
using Pane.Views;
using RectangleF = System.Drawing.RectangleF;

namespace Pane;

/// <summary>胶囊系统协调层（notes-mvp §6–7）：每条有内容的屏幕边复用一个队列宿主，
/// 全应用共用一个悬停预览宿主，悬浮项单独承载。折叠前必须可靠保存，失败保留编辑窗；
/// 拖动松手才提交布局；全屏避让只改临时层级，不改写置顶偏好。</summary>
public sealed partial class App
{
    readonly Dictionary<string, GlassPanel> capsuleQueues = [];
    readonly Dictionary<Guid, GlassPanel> capsuleFloats = [];
    GlassPanel? capsulePreviewPanel;
    NoteCapsulePreviewView? capsulePreviewView;
    DispatcherQueueTimer? capsuleHoverTimer;
    DispatcherQueueTimer? capsuleScrollCommit;
    DispatcherQueueTimer? capsuleFullscreenWatch;
    Guid? capsulePreviewNote, capsuleHoverNote, capsuleHighlightNote;
    bool capsulePreviewHovered;
    DispatcherQueueTimer? capsuleHideTimer;
    /// <summary>拖动期间临时从队列里隐藏的成员（galaxy 独立临时拖动胶囊：队列里不再显示原位条）。</summary>
    Guid? capsuleDragHidden;
    (string QueueKey, double Scroll)? pendingScrollCommit;
    readonly HashSet<string> capsuleDemotedMonitors = [];

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

    void RefreshCapsules()
    {
        if (closing || !desktopNotesEnabled) return;
        var settings = Store.Current;
        var pinned = settings.DesktopNotes ?? [];
        var active = new HashSet<string>();
        foreach (var group in (settings.NoteCapsules ?? [])
            .Where(pair => pair.Value is { Hidden: false, Slot: { } slot } && pinned.ContainsKey(pair.Key))
            .GroupBy(pair => (MonitorId: pair.Value.Slot!.MonitorId, Edge: (NoteEdge)pair.Value.Slot!.Edge)))
        {
            if (group.Key.Edge == NoteEdge.Float)
            {
                foreach (var pair in group) EnsureFloatHost(pair.Key, pair.Value.Slot!);
                continue;
            }
            var key = NoteCapsuleGeometry.QueueKey(group.Key.MonitorId, group.Key.Edge);
            active.Add(key);
            var members = group.Where(pair => pair.Key != capsuleDragHidden)
                .OrderBy(pair => pair.Value.Slot!.Order)
                .Select(pair => CapsuleMember(pair.Key)).ToList();
            EnsureQueueHost(key, group.Key.MonitorId, group.Key.Edge, members);
        }
        foreach (var (key, host) in capsuleQueues.ToArray())
            if (!active.Contains(key)) DisposeQueueHost(key, host);
        foreach (var (id, host) in capsuleFloats.ToArray())
            if (CapsuleState(id) is not { Hidden: false, Slot: { Edge: (int)NoteEdge.Float } } || !pinned.ContainsKey(id))
                DisposeFloatHost(id, host);
    }

    CapsuleMember CapsuleMember(Guid id)
    {
        var note = notes?.Find(id);
        var session = noteSessions?.Find(id);
        var title = (session?.Title ?? note?.Title ?? "").Trim();
        var due = GetNoteReminder(id);
        return new(id, title, due.HasValue, capsuleHighlightNote == id);
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
        var host = new GlassPanel(Strings.Notes_DesktopCard, queueView, queueView, Glass, desktopCard: true)
        {
            EscapeHides = false,
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
        host.HitRegion = () => QueueHitRegion(host, key, edge);
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
        host.SetTopmost(true);
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

    /// <summary>队列条带并集命中区域：胶囊间空隙可点穿（notes-mvp §6「透明间隙不挡桌面点击」）。</summary>
    nint QueueHitRegion(GlassPanel host, string key, NoteEdge edge)
    {
        var queue = QueueState(key, edge, []);
        var count = QueueMembersFromKey(key).Count;
        var vertical = NoteCapsuleGeometry.IsVertical(edge);
        var scale = host.DpiScale;
        var masterWidth = NoteCapsuleGeometry.MasterWidth(count);
        var pills = new List<RectangleF> { new(0, 0, masterWidth, NoteCapsuleGeometry.MasterHeight) };
        if (!queue.Collapsed)
            for (var index = 0; index < count; index++)
            {
                var along = (float)(masterWidth + NoteCapsuleGeometry.Gap + index * NoteCapsuleGeometry.Step(edge) - queue.Scroll);
                pills.Add(new(vertical ? 0 : along, vertical ? along : 0,
                    NoteCapsuleGeometry.CapsuleWidth, NoteCapsuleGeometry.CapsuleHeight));
            }
        nint region = Native.CreateRectRgn(0, 0, 0, 0);
        foreach (var pill in pills)
        {
            var next = Native.CreateRoundRectRgn((int)MathF.Round(pill.X * scale), (int)MathF.Round(pill.Y * scale),
                (int)MathF.Round((pill.X + pill.Width) * scale) + 1, (int)MathF.Round((pill.Y + pill.Height) * scale) + 1,
                (int)(14 * scale), (int)(14 * scale));
            Native.CombineRgn(region, region, next, 2);
            Native.DeleteObject(next);
        }
        return region;
    }

    void DisposeQueueHost(string key, GlassPanel host)
    {
        capsuleQueues.Remove(key);
        (host.View as IDisposable)?.Dispose();
        host.Dispose();
    }

    void DisposeFloatHost(Guid id, GlassPanel host)
    {
        capsuleFloats.Remove(id);
        (host.View as IDisposable)?.Dispose();
        host.Dispose();
    }

    // ---------------- 悬浮胶囊 ----------------

    void EnsureFloatHost(Guid id, NoteCapsuleSlot slot)
    {
        if (capsuleFloats.ContainsKey(id)) return;
        var view = new NoteCapsuleQueueView();
        // 悬浮胶囊单独承载：一个成员的独立队列视图，换边 / 隐藏 / 展开走同一套交互；
        // 阈值拖动同样交给全局幽灵胶囊（notes-mvp §7）。
        var floatKey = NoteCapsuleGeometry.QueueKey(slot.MonitorId, NoteEdge.Float);
        view.ExpandRequested += ExpandNoteFromCapsule;
        view.HideRequested += HideNote;
        view.DeleteRequested += DeleteNoteFromCapsule;
        view.DragStartedExternal += start => StartCapsuleDrag(floatKey, start.Id, start.Master);
        view.DragCompleted += result => DragCompleted(floatKey, result);
        view.HoverChanged += (noteId, index) => HoverCapsule(noteId, index);
        view.OpenManagerRequested += () => ShowNotes();
        view.NewNoteRequested += QuickNewDesktopNote;
        view.ShowAllRequested += ShowAllDesktopNotes;
        var monitor = GlassPanel.MonitorById(slot.MonitorId);
        var work = monitor is { } area ? WorkDip(area) : new RectangleF(0, 0, 1920, 1040);
        var position = NoteCapsuleGeometry.ClampFloat(work, slot.X, slot.Y);
        var host = new GlassPanel(Strings.Notes_DesktopCard, view, view, Glass, desktopCard: true)
        {
            EscapeHides = false,
            DesktopWorkAreaChanged = () => queue.TryEnqueue(() => { if (!closing) RefreshCapsules(); }),
        };
        WatchFrames(host);
        view.Apply(new(NoteCapsuleGeometry.QueueKey(slot.MonitorId, NoteEdge.Float), NoteEdge.Float, false, 0, 0,
            [CapsuleMember(id)]));
        capsuleFloats[id] = host;
        host.DesktopScreenPoint = new Native.Point
        {
            X = (int)((monitor?.Work.Left ?? 0) + position.X * (monitor?.Dpi ?? 1)),
            Y = (int)((monitor?.Work.Top ?? 0) + position.Y * (monitor?.Dpi ?? 1)),
        };
        host.RequestSize(new(NoteCapsuleGeometry.CapsuleWidth, NoteCapsuleGeometry.CapsuleHeight));
        host.DesktopAnchor = new(slot.MonitorId, position.X, position.Y);
        try { host.Show(activate: false); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            ReportFailure(Strings.Notes_DesktopCard, error);
            DisposeFloatHost(id, host);
            return;
        }
        host.SetTopmost(true);
        StartCapsuleFullscreenWatch();
    }

    // ---------------- 队列视图交互 ----------------

    void WireQueueView(string key, NoteCapsuleQueueView view)
    {
        view.ExpandRequested += ExpandNoteFromCapsule;
        view.HideRequested += HideNote;
        view.DeleteRequested += DeleteNoteFromCapsule;
        view.FloatRequested += FloatNote;
        view.MoveEdgeRequested += MoveNoteToEdge;
        view.MasterToggleRequested += () => ToggleQueue(key);
        view.DragStartedExternal += start => StartCapsuleDrag(key, start.Id, start.Master);
        view.DragCompleted += result => DragCompleted(key, result);
        view.ScrollDelta += delta => ScrollQueue(key, delta);
        view.HoverChanged += HoverCapsule;
        view.OpenManagerRequested += () => ShowNotes();
        view.NewNoteRequested += QuickNewDesktopNote;
        view.ShowAllRequested += ShowAllDesktopNotes;
        view.QueueMoveRequested += edge => MoveQueue(key, edge);
    }

    void ExpandNoteFromCapsule(Guid id)
    {
        // 点击普通胶囊：打开或聚焦同 ID 的便签，恢复展开位置和尺寸，保留胶囊入口（notes-mvp §6）。
        if (CapsuleState(id) is not { } layout) return;
        if (layout.Hidden) SetCapsuleState(id, layout with { Hidden = false });
        var current = CapsuleState(id) ?? new NoteCapsuleLayout(false, false, null);
        SetCapsuleState(id, current with { Folded = false, Hidden = false });
        RefreshDesktopNotes();
        RefreshCapsules();
        FocusDesktopNote(id);
    }

    void HideNote(Guid id)
    {
        if (CapsuleState(id) is not { } layout) return;
        SetCapsuleState(id, layout with { Hidden = true });
        if (desktopNotes.Remove(id, out var removed))
        {
            (removed.View as IDisposable)?.Dispose();
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

    void FloatNote(Guid id)
    {
        // 拖为悬浮需要先折叠（notes-mvp §6）；悬浮坐标由松手落点或原落点推导。
        if (desktopNotes.ContainsKey(id)) return;
        var layout = CapsuleState(id);
        if (layout?.Slot is not { } slot) return;
        SetCapsuleState(id, new(true, false, slot with { Edge = (int)NoteEdge.Float }));
        RefreshCapsules();
    }

    void MoveNoteToEdge(Guid id, NoteEdge edge)
    {
        var layout = CapsuleState(id);
        var slot = layout?.Slot;
        var monitorId = slot?.MonitorId ?? "";
        if (GlassPanel.MonitorById(monitorId) == null && Native.GetCursorPos(out var point))
            monitorId = GlassPanel.MonitorAt(point.X, point.Y)?.Id ?? monitorId;
        var order = QueueMembers(monitorId, edge).Count;
        SetCapsuleState(id, new(true, false, new(monitorId, (int)edge, 0, 0, order)));
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
        // 收起后宿主只剩主胶囊；重摆窗口大小（notes-mvp §6「整队收起仍保留主胶囊」）。
        var (monitorId, edge) = SplitQueueKey(key);
        var members = QueueMembers(monitorId, edge).Count;
        if (capsuleQueues.TryGetValue(key, out var host))
        {
            host.RequestSize(QueueHostSize(key, edge, collapsed ? 0 : members));
            host.DesktopAnchor = QueueHostAnchor(key, edge, collapsed ? 0 : members);
            GlassPanel.RestoreDesktopCards([host]);
        }
    }

    float DefaultStartFromKey(string key)
    {
        var (monitorId, edge) = SplitQueueKey(key);
        return DefaultStartFor(monitorId, edge);
    }

    void DragCompleted(string key, CapsuleDragResult result) =>
        DropCapsule(key, result.Id, result.Master, (int)result.ScreenX, (int)result.ScreenY);

    /// <summary>拖动落点（屏幕物理坐标）：换屏 / 换边停靠、远离边缘悬浮、队内重排、主胶囊改队列起点。
    /// 供 XAML 捕获路径和全局幽灵胶囊路径共用。</summary>
    void DropCapsule(string key, Guid id, bool master, int screenX, int screenY)
    {
        var (monitorId, edge) = SplitQueueKey(key);
        var monitor = GlassPanel.MonitorById(monitorId);
        var work = monitor is { } area ? WorkDip(area) : new RectangleF(0, 0, 1920, 1040);
        if (master)
        {
            var members = QueueMembers(monitorId, edge).Count;
            var alongMaster = NoteCapsuleGeometry.IsVertical(edge) ? screenY : screenX;
            var origin = monitor is { } masterArea
                ? (NoteCapsuleGeometry.IsVertical(edge) ? masterArea.Work.Top : masterArea.Work.Left) / masterArea.Dpi
                : 0;
            CommitQueueStart(key, NoteCapsuleGeometry.ClampStart(work, edge,
                alongMaster / (monitor?.Dpi ?? 1) - origin, members, members));
            return;
        }
        var targetMonitor = GlassPanel.MonitorAt(screenX, screenY);
        if (targetMonitor is not { } targetArea) return;
        var targetWork = WorkDip(targetArea);
        var targetPoint = new PointF(
            (screenX - targetArea.Work.Left) / targetArea.Dpi,
            (screenY - targetArea.Work.Top) / targetArea.Dpi);
        var target = NoteCapsuleGeometry.NearestEdge(targetWork, targetPoint);
        var edgeDistance = Math.Min(Math.Abs(targetPoint.X - targetWork.Left),
            Math.Min(Math.Abs(targetWork.Right - targetPoint.X), Math.Abs(targetPoint.Y - targetWork.Top)));
        if (edgeDistance > 72)
        {
            var x = targetPoint.X - NoteCapsuleGeometry.CapsuleWidth / 2;
            var y = targetPoint.Y - NoteCapsuleGeometry.CapsuleHeight / 2;
            SetCapsuleState(id, new(true, false,
                new(targetArea.Id, (int)NoteEdge.Float, x, y, QueueMembers(targetArea.Id, NoteEdge.Float).Count)));
            RefreshCapsules();
            return;
        }
        if (target != edge || targetArea.Id != monitorId)
        {
            var order = QueueMembers(targetArea.Id, target).Count;
            SetCapsuleState(id, new(true, false,
                new(targetArea.Id, (int)target, 0, 0, order)));
            RefreshCapsules();
            return;
        }
        var queue = QueueState(key, edge, []);
        var along = NoteCapsuleGeometry.IsVertical(edge) ? targetPoint.Y : targetPoint.X;
        var index = NoteCapsuleGeometry.InsertIndex(edge, queue.Start, queue.Scroll, along, QueueMembers(monitorId, edge).Count);
        ReorderNote(key, id, index);
    }

    // ---------------- 全局拖动胶囊（notes-mvp §7「独立的临时拖动胶囊」） ----------------

    GlassPanel? capsuleDragGhost;
    NoteCapsuleGhostView? capsuleDragGhostView;
    DispatcherQueueTimer? capsuleDragPoll;
    (string Key, Guid Id, bool Master)? capsuleDrag;

    /// <summary>阈值拖动开始：队列视图交出控制权，幽灵胶囊 + GetCursorPos 轮询接管，
    /// 跨屏拖动不丢捕获；Esc 取消，左键松开提交落点。</summary>
    void StartCapsuleDrag(string key, Guid id, bool master)
    {
        if (closing) return;
        HideCapsulePreview();
        EndCapsuleDrag();
        var member = CapsuleMember(id);
        var title = master
            ? string.Format(Strings.Notes_CapsuleCount, (object?)QueueMembersFromKey(key).Count)
            : member.Title.Length > 0 ? member.Title : Strings.Notes_Untitled;
        NoteCapsuleGhostView? ghostView = null;
        GlassPanel? ghost = null;
        try
        {
            ghostView = new NoteCapsuleGhostView();
            ghostView.Apply(title);
            ghost = new GlassPanel(Strings.Notes_DesktopCard, ghostView, ghostView, Glass, desktopCard: true)
            { EscapeHides = false };
            WatchFrames(ghost);
            Native.GetCursorPos(out var cursor);
            ghost.RequestSize(ghostView.PreferredSize);
            ghost.Show(activate: false);
            ghost.SetTopmost(true);
            ghost.MoveTo(cursor.X + 12, cursor.Y + 10);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            (ghost?.View as IDisposable)?.Dispose();
            ghost?.Dispose();
            ReportFailure(Strings.Notes_DesktopCard, error);
            return;
        }
        capsuleDragGhostView = ghostView;
        capsuleDragGhost = ghost;
        capsuleDrag = (key, id, master);
        // 队列里临时隐藏被拖成员，只留幽灵胶囊跟手（galaxy 独立临时拖动胶囊）。
        capsuleDragHidden = master ? null : id;
        RefreshCapsules();
        capsuleDragPoll ??= CreateCapsuleDragPoll();
        capsuleDragPoll.Stop();
        capsuleDragPoll.Start();
    }

    DispatcherQueueTimer CreateCapsuleDragPoll()
    {
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(12);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (capsuleDrag is not { } drag || closing || capsuleDragGhost is not { } ghost)
            {
                EndCapsuleDrag();
                return;
            }
            Native.GetCursorPos(out var cursor);
            ghost.MoveTo(cursor.X + 12, cursor.Y + 10);
            if ((GetAsyncKeyStateForDrag(0x1B) & 0x8000) != 0)
            {
                // Esc 取消：恢复原布局，不误触任何动作（notes-mvp §7）。
                var state = capsuleDrag;
                EndCapsuleDrag();
                if (state is { } cancelled) RefreshCapsules();
                return;
            }
            if ((GetAsyncKeyStateForDrag(1) & 0x8000) == 0)
            {
                var key = drag.Key;
                var id = drag.Id;
                var master = drag.Master;
                EndCapsuleDrag();
                DropCapsule(key, id, master, cursor.X, cursor.Y);
            }
        };
        return timer;
    }

    void EndCapsuleDrag()
    {
        capsuleDragPoll?.Stop();
        capsuleDrag = null;
        capsuleDragHidden = null;
        if (capsuleDragGhost is { } ghost)
        {
            (ghost.View as IDisposable)?.Dispose();
            ghost.Dispose();
            capsuleDragGhost = null;
            capsuleDragGhostView = null;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    static extern short GetAsyncKeyStateForDrag(int key);

    void CommitQueueStart(string key, float start)
    {
        Store.Update(settings =>
        {
            var queues = new Dictionary<string, NoteCapsuleQueue>(settings.NoteCapsuleQueues ?? []);
            var current = queues.GetValueOrDefault(key) ?? new(DefaultStartFromKey(key), false, 0);
            queues[key] = current with { Start = start };
            return settings with { NoteCapsuleQueues = queues };
        });
        RefreshCapsules();
    }

    void MoveQueue(string key, NoteEdge edge)
    {
        // 整队换边 / 换屏：提交前重新检查成员，已隐藏或删除的项跳过（notes-mvp §6）。
        var (monitorId, current) = SplitQueueKey(key);
        if (current == edge) return;
        var start = DefaultStartFor(monitorId, edge);
        var order = 0;
        Store.Update(settings =>
        {
            var capsules = new Dictionary<Guid, NoteCapsuleLayout>(settings.NoteCapsules ?? []);
            var members = capsules.Where(pair => pair.Value is { Hidden: false, Slot: { } slot } &&
                NoteCapsuleGeometry.QueueKey(slot.MonitorId, (NoteEdge)slot.Edge) == key &&
                (settings.DesktopNotes ?? []).ContainsKey(pair.Key))
                .OrderBy(pair => pair.Value.Slot!.Order).ToList();
            foreach (var pair in members)
                capsules[pair.Key] = pair.Value with { Slot = pair.Value.Slot! with { Edge = (int)edge, Order = order++ } };
            var queues = new Dictionary<string, NoteCapsuleQueue>(settings.NoteCapsuleQueues ?? []);
            queues.Remove(key);
            queues[NoteCapsuleGeometry.QueueKey(monitorId, edge)] = new(start, false, 0);
            return settings with { NoteCapsules = capsules, NoteCapsuleQueues = queues };
        });
        RefreshCapsules();
    }

    void ScrollQueue(string key, double delta)
    {
        var (monitorId, edge) = SplitQueueKey(key);
        var monitor = GlassPanel.MonitorById(monitorId);
        var work = monitor is { } area ? WorkDip(area) : new RectangleF(0, 0, 1920, 1040);
        var members = QueueMembers(monitorId, edge).Count;
        var queue = QueueState(key, edge, []);
        var scroll = NoteCapsuleGeometry.ClampScroll(work, edge, queue.Start, queue.Scroll + delta, members, members);
        if (capsuleQueues.TryGetValue(key, out var host) && host.View is NoteCapsuleQueueView view)
            view.Apply(queue with { Scroll = scroll });
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
        NoteCapsuleSlot slot = layout?.Slot ?? FoldSlot(id);
        SetCapsuleState(id, new(true, false, slot));
        if (desktopNotes.Remove(id, out var removed))
        {
            // 折叠过渡：先播收起动画再销毁正文窗（notes-mvp §6；缩向目标边缘的方向性过渡留待手测打磨）。
            if (removed.Shown)
            {
                removed.Hidden += (_, _) => queue.TryEnqueue(() =>
                {
                    (removed.View as IDisposable)?.Dispose();
                    removed.Dispose();
                });
                removed.Hide();
            }
            else
            {
                (removed.View as IDisposable)?.Dispose();
                removed.Dispose();
            }
        }
        RefreshDesktopNotes();
        RefreshCapsules();
        ShowCapsuleFoldHint();
    }

    void ReportNoteSaveFailure(Guid id)
    {
        if (desktopNotes.TryGetValue(id, out var panel) && panel.View is DesktopNoteView view) view.ReportSaveFailed();
    }

    /// <summary>首次折叠落点：当前屏幕最近的左 / 右 / 顶部边缘，追加到队尾（notes-mvp §6）。</summary>
    NoteCapsuleSlot FoldSlot(Guid id)
    {
        var point = new Native.Point();
        if (desktopNotes.TryGetValue(id, out var panel))
        {
            var origin = panel.Origin;
            point = new Native.Point { X = origin.X + (int)(panel.Width * panel.DpiScale) / 2, Y = origin.Y + 16 };
        }
        else if (Native.GetCursorPos(out var cursor)) point = cursor;
        var monitor = GlassPanel.MonitorAt(point.X, point.Y);
        var work = monitor is { } area ? WorkDip(area) : new RectangleF(0, 0, 1920, 1040);
        var edge = NoteCapsuleGeometry.NearestEdge(work, new PointF(point.X, point.Y));
        var order = QueueMembers(monitor?.Id ?? "", edge).Count;
        return new(monitor?.Id ?? "", (int)edge, 0, 0, order);
    }

    /// <summary>首次折叠：一次性提示说明胶囊位置和找回方式（notes-mvp §6）。</summary>
    void ShowCapsuleFoldHint()
    {
        if (Store.Current.NoteCapsuleHintShown) return;
        Store.Update(settings => settings with { NoteCapsuleHintShown = true });
        var text = new TextBlock
        {
            Text = string.Format(Strings.Notes_CapsuleFirstFoldHint, Strings.Notes_ShowDesktopNotes),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Margin = new(14, 12, 14, 12),
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["PaneFontUi"],
            FontSize = 13, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["PaneText1Brush"],
        };
        var host = new GlassPanel(Strings.Notes_DesktopCard, text, new HintPanelView(text, new(340, 68)), Glass, desktopCard: true)
        { EscapeHides = false };
        WatchFrames(host);
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(6);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            (host.View as IDisposable)?.Dispose();
            host.Dispose();
        };
        try { host.Show(activate: false); host.SetTopmost(true); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            (host.View as IDisposable)?.Dispose();
            host.Dispose();
        }
    }

    sealed class HintPanelView(TextBlock text, Windows.Foundation.Size size) : IPanelView
    {
        public Windows.Foundation.Size PreferredSize => size;
        public UIElement DragRegion => text;
        public event EventHandler<Windows.Foundation.Size>? SizeRequested { add { } remove { } }
        public event EventHandler? CloseRequested { add { } remove { } }
        public void OnShowing(PanelShowContext context) { }
        public void OnShown() { }
        public void OnHidden() { }
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
        hub.Attach(session);
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
        FocusDesktopNote(session.Id, focusBody: true);
    }

    void FocusDesktopNote(Guid id, bool focusBody = false)
    {
        if (!desktopNotes.TryGetValue(id, out var panel)) return;
        panel.BringToFront(activate: true);
        if (focusBody && panel.View is DesktopNoteView view) view.FocusBody();
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
        capsulePreviewView ??= new NoteCapsulePreviewView();
        capsulePreviewView.Apply(id, title, body);
        if (capsulePreviewPanel is { } host)
        {
            (host.View as IDisposable)?.Dispose();
            host.Dispose();
        }
        var view = capsulePreviewView;
        var host2 = new GlassPanel(Strings.Notes_DesktopCard, view, view, Glass, desktopCard: true) { EscapeHides = false };
        WatchFrames(host2);
        capsulePreviewPanel = host2;
        // 预览卡自身的悬停参与走廊判定（notes-mvp §7）。
        view.HoverChanged += PreviewHoverChanged;
        if (PreviewPlacement(id, host2) is not { } placement) { HideCapsulePreview(); return; }
        host2.DesktopScreenPoint = placement.Screen;
        host2.DesktopAnchor = placement.Anchor;
        host2.RequestSize(view.PreferredSize);
        try
        {
            host2.Show(activate: false);
            host2.SetTopmost(true);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            (host2.View as IDisposable)?.Dispose();
            host2.Dispose();
            capsulePreviewPanel = null;
        }
    }

    /// <summary>预览定位：顶部向下展开、左右向屏幕内侧展开，紧贴胶囊（Gap 即窄通道）。</summary>
    (Native.Point Screen, LauncherPosition Anchor)? PreviewPlacement(Guid id, GlassPanel previewHost)
    {
        foreach (var (key, host) in capsuleQueues.Select(pair => (pair.Key, pair.Value))
            .Concat(capsuleFloats.Select(pair => (pair.Key.ToString(), pair.Value))))
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
        RestoreCapsuleZOrder();
    }

    void AvoidFullscreenWindows()
    {
        if (closing) return;
        var foreground = Native.GetForegroundWindow();
        if (foreground == 0 || OwnWindow(foreground)) { RestoreCapsuleZOrder(); return; }
        Native.GetWindowRect(foreground, out var bounds);
        var monitor = GlassPanel.MonitorFromPoint(new GlassPanel.Point { X = bounds.Left + 8, Y = bounds.Top + 8 }, 2);
        var info = new GlassPanel.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<GlassPanel.MonitorInfo>() };
        if (!GlassPanel.GetMonitorInfoW(monitor, ref info)) return;
        // 全屏窗口覆盖整块显示器（不只是工作区）才算；最小化 / 普通最大化不触发。
        if (bounds.Left > info.Monitor.Left || bounds.Top > info.Monitor.Top ||
            bounds.Right < info.Monitor.Right || bounds.Bottom < info.Monitor.Bottom)
        {
            RestoreCapsuleZOrder();
            return;
        }
        var monitorId = GlassPanel.MonitorAt(info.Monitor.Left + 16, info.Monitor.Top + 16)?.Id;
        if (monitorId == null) return;
        // 该屏的胶囊、预览和开了置顶的展开便签让到全屏窗之后；其他屏保留原状态（notes-mvp §7）。
        if (capsuleDemotedMonitors.Add(monitorId))
            foreach (var host in CapsulePanelsOn(monitorId))
                Native.SetWindowPos(host.Input, -2, 0, 0, 0, 0, 0x13);
    }

    void RestoreCapsuleZOrder()
    {
        if (capsuleDemotedMonitors.Count == 0) return;
        capsuleDemotedMonitors.Clear();
        foreach (var host in capsuleQueues.Values.Concat(capsuleFloats.Values))
            if (host.Shown) host.SetTopmost(true);
        if (capsulePreviewPanel is { } preview && preview.Shown) preview.SetTopmost(true);
        // 展开便签按保存的置顶偏好恢复（不改写偏好本身）。
        foreach (var (id, host) in desktopNotes)
            host.SetTopmost(Store.Current.DesktopNoteTopmost?.GetValueOrDefault(id) == true);
    }

    IEnumerable<GlassPanel> CapsulePanelsOn(string monitorId)
    {
        foreach (var edge in new[] { NoteEdge.Left, NoteEdge.Right, NoteEdge.Top })
            if (capsuleQueues.TryGetValue(NoteCapsuleGeometry.QueueKey(monitorId, edge), out var host))
                yield return host;
        foreach (var (id, host) in capsuleFloats)
            if (CapsuleState(id)?.Slot is { } slot && slot.MonitorId == monitorId)
                yield return host;
        if (capsulePreviewPanel is { } preview) yield return preview;
        foreach (var (id, host) in desktopNotes)
            if (host.DesktopPlacement?.MonitorId == monitorId && Store.Current.DesktopNoteTopmost?.GetValueOrDefault(id) == true)
                yield return host;
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
        capsuleHoverTimer?.Stop();
        foreach (var host in capsuleQueues.Values.Concat(capsuleFloats.Values))
        {
            (host.View as IDisposable)?.Dispose();
            host.Dispose();
        }
        capsuleQueues.Clear();
        capsuleFloats.Clear();
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
