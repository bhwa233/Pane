using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Hosting;
using Pane.Localization;
using Pane.Views.Controls;
using Windows.Foundation;

namespace Pane.Views;

/// <summary>胶囊成员的渲染模型：协调层按设置推导后交给视图。</summary>
internal sealed record CapsuleMember(Guid Id, string Title, bool Reminder, bool Highlighted);

/// <summary>一条贴边队列的渲染状态（notes-mvp §6）。Start/Scroll 与持久化模型一致用 double。</summary>
internal sealed record CapsuleQueueState(string QueueKey, NoteEdge Edge, bool Collapsed,
    double Start, double Scroll, IReadOnlyList<CapsuleMember> Members);

/// <summary>拖动松手结果：协调层把本地 DIP 坐标换算到工作区后决定重排 / 换边 / 悬浮 / 整队迁移。</summary>
internal sealed record CapsuleDragResult(Guid Id, bool Master, double LocalX, double LocalY, double ScreenX, double ScreenY);

/// <summary>超过阈值的全局拖动（notes-mvp §7「独立的临时拖动胶囊」）：队列视图就此交出控制权，
/// 协调层用幽灵胶囊 + GetCursorPos 轮询接管，跨屏拖动不丢捕获。</summary>
internal sealed record CapsuleDragStart(Guid Id, bool Master);

/// <summary>贴边胶囊队列（notes-mvp §6–7）：主胶囊 + 成员条。点击展开、×隐藏、右键换边与悬浮、
/// 拖动重排（5px 阈值，松手才提交，取消 / 丢捕获恢复原布局）、滚轮滚动溢出、悬停请求只读预览。
/// 视图只管交互与摆放；几何、持久化和窗口层级都在协调层。</summary>
public sealed partial class NoteCapsuleQueueView : UserControl, IPanelView
{
    readonly Dictionary<Guid, Border> strips = [];
    CapsuleQueueState? state;
    bool dragging, dragMaster, externalDrag;
    Guid dragId;
    double pressX, pressY;

    public event Action<Guid>? ExpandRequested;
    public event Action<Guid>? HideRequested;
    internal event Action<Guid, NoteEdge>? MoveEdgeRequested;
    public event Action<Guid>? FloatRequested;
    public event Action<Guid>? DeleteRequested;
    public event Action? MasterToggleRequested;
    internal event Action<CapsuleDragResult>? DragCompleted;
    internal event Action<CapsuleDragStart>? DragStartedExternal;
    public event Action<double>? ScrollDelta;
    public event Action<Guid, int?>? HoverChanged;
    public event Action? OpenManagerRequested;
    public event Action? NewNoteRequested;
    public event Action? ShowAllRequested;

    public NoteCapsuleQueueView()
    {
        InitializeComponent();
        PointerWheelChanged += WheelChanged;
    }

    public Size PreferredSize => new(NoteCapsuleGeometry.CapsuleWidth, NoteCapsuleGeometry.MasterHeight);
    public UIElement DragRegion => Surface;
    event EventHandler<Size>? IPanelView.SizeRequested { add { } remove { } }
    event EventHandler? IPanelView.CloseRequested { add { } remove { } }
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { }

    /// <summary>按协调层给的快照重建队列；滚动与收放状态包含在内。</summary>
    internal void Apply(CapsuleQueueState next)
    {
        state = next;
        externalDrag = false;
        dragging = false;
        dragId = Guid.Empty;
        Surface.Children.Clear();
        strips.Clear();
        Surface.Children.Add(BuildMaster(next));
        if (next.Collapsed) return;
        var index = 0;
        foreach (var member in next.Members)
        {
            var strip = BuildMember(member);
            var along = NoteCapsuleGeometry.MasterWidth(next.Members.Count) + NoteCapsuleGeometry.Gap +
                index * NoteCapsuleGeometry.Step(next.Edge) - next.Scroll;
            Canvas.SetLeft(strip, next.Edge == NoteEdge.Top ? along : 0);
            Canvas.SetTop(strip, next.Edge == NoteEdge.Top ? 0 : along);
            strips[member.Id] = strip;
            Surface.Children.Add(strip);
            index++;
        }
    }

    // ---------------- 主胶囊 ----------------

    Border BuildMaster(CapsuleQueueState queue)
    {
        var width = NoteCapsuleGeometry.MasterWidth(queue.Members.Count);
        var pill = new Border
        {
            Width = width,
            Height = NoteCapsuleGeometry.MasterHeight,
            CornerRadius = new CornerRadius(14),
            Background = FillProto.Background,
            BorderBrush = FillProto.BorderBrush,
            BorderThickness = new Thickness(1),
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var chevron = new PaneIcon { Glyph = "chev-r", Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Text2Proto.Foreground, RenderTransform = MasterChevronRotation(queue.Edge, queue.Collapsed) };
        content.Children.Add(chevron);
        var count = new TextBlock { Text = queue.Members.Count.ToString(), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, FontFamily = (FontFamily)Application.Current.Resources["PaneFontMono"],
            FontSize = 12, Foreground = TextProto.Foreground };
        Grid.SetColumn(count, 1);
        content.Children.Add(count);
        pill.Child = content;
        var toggleLabel = queue.Collapsed ? Strings.Notes_CapsulesExpand : Strings.Notes_CapsulesCollapse;
        AutomationProperties.SetName(pill, string.Format(Strings.Notes_CapsuleCount, (object?)queue.Members.Count) + "；" + toggleLabel);
        ToolTipService.SetToolTip(pill, toggleLabel);
        HookStrip(pill, Guid.Empty, master: true);
        pill.RightTapped += (_, args) => { ShowMasterMenu(pill, args); args.Handled = true; };
        return pill;
    }

    /// <summary>图标表示点击后的动作：展开态给「收起」箭头，收起态给「展开」箭头。</summary>
    static RotateTransform MasterChevronRotation(NoteEdge edge, bool collapsed)
    {
        if (NoteCapsuleGeometry.IsVertical(edge)) return new RotateTransform { Angle = collapsed ? 90 : -90 };
        return new RotateTransform { Angle = collapsed ? 0 : 180 };
    }

    // ---------------- 成员胶囊 ----------------

    Border BuildMember(CapsuleMember member)
    {
        var pill = new Border
        {
            Width = NoteCapsuleGeometry.CapsuleWidth,
            Height = NoteCapsuleGeometry.CapsuleHeight,
            CornerRadius = new CornerRadius(14),
            Background = FillProto.Background,
            BorderBrush = member.Highlighted ? AccentProto.Foreground : FillProto.BorderBrush,
            BorderThickness = new Thickness(member.Highlighted ? 1.6 : 1),
        };
        var content = new Grid { ColumnSpacing = 6 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (member.Reminder)
        {
            var badge = new PaneIcon { Glyph = "timer", Width = 11, Height = 11, VerticalAlignment = VerticalAlignment.Center,
                Foreground = member.Highlighted ? AccentProto.Foreground : Text2Proto.Foreground };
            AutomationProperties.SetAccessibilityView(badge, AccessibilityView.Raw);
            content.Children.Add(badge);
        }
        var title = new TextBlock
        {
            Text = member.Title, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = TextProto.Foreground,
        };
        Grid.SetColumn(title, 1);
        content.Children.Add(title);
        var hide = new Button { Style = (Style)Application.Current.Resources["PaneIconButton"],
            Width = 20, Height = 20, IsTabStop = false, Content = new PaneIcon { Glyph = "close", Width = 9, Height = 9 } };
        AutomationProperties.SetName(hide, Strings.Notes_CapsuleHide);
        ToolTipService.SetToolTip(hide, Strings.Notes_CapsuleHide);
        hide.Click += (_, _) => HideRequested?.Invoke(member.Id);
        Grid.SetColumn(hide, 2);
        content.Children.Add(hide);
        pill.Child = content;
        var name = member.Title.Length == 0 ? Strings.Notes_Untitled : member.Title;
        AutomationProperties.SetName(pill, name);
        ToolTipService.SetToolTip(pill, name);
        pill.RightTapped += (_, args) => { ShowMemberMenu(pill, member.Id, args); args.Handled = true; };
        HookStrip(pill, member.Id, master: false);
        return pill;
    }

    void ShowMemberMenu(Border strip, Guid id, RightTappedRoutedEventArgs args)
    {
        var menu = new MenuFlyout();
        var expand = new MenuFlyoutItem { Text = Strings.Notes_OpenEditor };
        expand.Click += (_, _) => ExpandRequested?.Invoke(id);
        menu.Items.Add(expand);
        menu.Items.Add(EdgeMoveMenu(id, wholeQueue: false));
        if (state is { } queue)
        {
            var floating = new MenuFlyoutItem { Text = Strings.Notes_CapsuleFloat };
            floating.Click += (_, _) => FloatRequested?.Invoke(id);
            menu.Items.Add(floating);
        }
        var hide = new MenuFlyoutItem { Text = Strings.Notes_CapsuleHide };
        hide.Click += (_, _) => HideRequested?.Invoke(id);
        menu.Items.Add(hide);
        var danger = new MenuFlyoutSubItem { Text = Strings.Notes_DeleteCard };
        var confirm = new MenuFlyoutItem { Text = Strings.Notes_DeleteRowConfirm };
        confirm.Click += (_, _) => DeleteRequested?.Invoke(id);
        danger.Items.Add(confirm);
        menu.Items.Add(danger);
        menu.ShowAt(strip, args.GetPosition(strip));
    }

    void ShowMasterMenu(Border strip, RightTappedRoutedEventArgs args)
    {
        var menu = new MenuFlyout();
        var manager = new MenuFlyoutItem { Text = Strings.Notes_OpenManager };
        manager.Click += (_, _) => OpenManagerRequested?.Invoke();
        menu.Items.Add(manager);
        var create = new MenuFlyoutItem { Text = Strings.Notes_New };
        create.Click += (_, _) => NewNoteRequested?.Invoke();
        menu.Items.Add(create);
        var showAll = new MenuFlyoutItem { Text = Strings.Notes_ShowDesktopNotes };
        showAll.Click += (_, _) => ShowAllRequested?.Invoke();
        menu.Items.Add(showAll);
        if (state is { } queue) menu.Items.Add(EdgeMoveMenu(Guid.Empty, wholeQueue: true));
        menu.ShowAt(strip, args.GetPosition(strip));
    }

    MenuFlyoutSubItem EdgeMoveMenu(Guid id, bool wholeQueue)
    {
        var menu = new MenuFlyoutSubItem { Text = wholeQueue ? Strings.Notes_DesktopCard : Strings.Notes_PinDesktop };
        void Add(NoteEdge edge, string label)
        {
            if (state is { } queue && queue.Edge == edge) return;
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) =>
            {
                if (wholeQueue) QueueMoveRequested?.Invoke(edge);
                else MoveEdgeRequested?.Invoke(id, edge);
            };
            menu.Items.Add(item);
        }
        Add(NoteEdge.Left, Strings.Notes_MoveEdgeLeft);
        Add(NoteEdge.Right, Strings.Notes_MoveEdgeRight);
        Add(NoteEdge.Top, Strings.Notes_MoveEdgeTop);
        return menu;
    }

    internal event Action<NoteEdge>? QueueMoveRequested;

    // ---------------- 指针交互：点击 / 拖动 / 悬停 / 滚轮 ----------------

    void HookStrip(Border pill, Guid id, bool master)
    {
        pill.PointerPressed += (sender, args) =>
        {
            if (state is not { } queue) return;
            var point = args.GetCurrentPoint(Surface);
            if (!point.Properties.IsLeftButtonPressed) return;
            dragId = id;
            dragMaster = master;
            pressX = point.Position.X;
            pressY = point.Position.Y;
            dragging = false;
            pill.CapturePointer(args.Pointer);
            args.Handled = true;
        };
        pill.PointerMoved += (sender, args) =>
        {
            if (state is not { } queue || dragId != id) return;
            var point = args.GetCurrentPoint(Surface);
            if (!point.Properties.IsLeftButtonPressed) return;
            var delta = NoteCapsuleGeometry.IsVertical(queue.Edge) ? point.Position.Y - pressY : point.Position.X - pressX;
            if (!dragging)
            {
                if (Math.Abs(delta) <= 5) return;
                // 超过阈值即交出控制权（galaxy 独立临时拖动胶囊）：释放捕获，改由协调层的幽灵胶囊全局跟手。
                dragging = true;
                externalDrag = true;
                (sender as UIElement)?.ReleasePointerCapture(args.Pointer);
                DragStartedExternal?.Invoke(new CapsuleDragStart(id, dragMaster));
                args.Handled = true;
                return;
            }
            args.Handled = true;
        };
        pill.PointerReleased += (sender, args) =>
        {
            if (dragId != id) return;
            var point = args.GetCurrentPoint(Surface);
            (sender as UIElement)?.ReleasePointerCapture(args.Pointer);
            dragId = Guid.Empty;
            if (externalDrag)
            {
                // 外部拖动引擎接管落点：这里只吞掉松手，避免误触展开（notes-mvp §7）。
                dragging = false;
                args.Handled = true;
                return;
            }
            if (!dragging)
            {
                if (dragMaster) MasterToggleRequested?.Invoke();
                else if (!master) ExpandRequested?.Invoke(id);
                return;
            }
            dragging = false;
            Native.GetCursorPos(out var screen);
            DragCompleted?.Invoke(new CapsuleDragResult(id, dragMaster, point.Position.X, point.Position.Y, screen.X, screen.Y));
            args.Handled = true;
        };
        pill.PointerCaptureLost += (_, _) => CancelDrag();
        if (!master)
        {
            pill.PointerEntered += (_, _) =>
            {
                if (dragging) return;
                HoverChanged?.Invoke(id, IndexOf(id));
            };
            pill.PointerExited += (_, _) => HoverChanged?.Invoke(id, null);
        }
    }

    int? IndexOf(Guid id)
    {
        if (state is not { } queue) return null;
        for (var index = 0; index < queue.Members.Count; index++)
            if (queue.Members[index].Id == id) return index;
        return null;
    }

    /// <summary>成员在队列里的位次（预览定位 / 重排提交用）。</summary>
    public int? MemberIndex(Guid id) => IndexOf(id);

    /// <summary>成员条的本地 DIP 坐标（悬停预览换算到屏幕用）。</summary>
    public (double X, double Y)? MemberOffset(Guid id) =>
        strips.TryGetValue(id, out var strip) ? (Canvas.GetLeft(strip), Canvas.GetTop(strip)) : null;

    /// <summary>拖动取消 / 丢失捕获：恢复原布局，无意外点击（notes-mvp §7）。
    /// 外部拖动已由协调层的幽灵胶囊接管时不在此处重置。</summary>
    void CancelDrag()
    {
        if (externalDrag) { dragId = Guid.Empty; return; }
        var active = dragging;
        dragging = false;
        dragId = Guid.Empty;
        if (active && state is { } queue) Apply(queue);
    }

    void WheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (state is not { } queue || queue.Collapsed) return;
        var delta = args.GetCurrentPoint(Surface).Properties.MouseWheelDelta;
        if (delta == 0) return;
        // 顶部队列：普通滚轮转横向滚动；左右队列天然纵向。
        var effective = NoteCapsuleGeometry.IsVertical(queue.Edge) ? delta : -delta;
        ScrollDelta?.Invoke(effective / 40.0);
        args.Handled = true;
    }
}
