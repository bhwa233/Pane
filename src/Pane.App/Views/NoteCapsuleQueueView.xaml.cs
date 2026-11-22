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
internal sealed record CapsuleMember(Guid Id, string Title, bool Reminder, bool Highlighted, bool Topmost, bool DesktopPinned);

/// <summary>一条贴边队列的渲染状态（notes-mvp §6）。Start/Scroll 与持久化模型一致用 double。</summary>
internal sealed record CapsuleQueueState(string QueueKey, NoteEdge Edge, bool Collapsed,
    double Start, double Scroll, IReadOnlyList<CapsuleMember> Members);

/// <summary>拖动松手结果：协调层把本地 DIP 坐标换算到工作区后决定重排 / 换边 / 悬浮 / 整队迁移。</summary>
internal sealed record CapsuleDragResult(Guid Id, bool Master, double LocalX, double LocalY, double ScreenX, double ScreenY);


/// <summary>贴边胶囊队列（notes-mvp §6–7）：主胶囊 + 成员条。点击展开、×隐藏、右键换边与悬浮、
/// 拖动重排（5px 阈值，松手才提交，取消 / 丢捕获恢复原布局）、滚轮滚动溢出、悬停请求只读预览。
/// 视图只管交互与摆放；几何、持久化和窗口层级都在协调层。</summary>
public sealed partial class NoteCapsuleQueueView : UserControl, IPanelView
{
    readonly Dictionary<Guid, (Border Strip, TextBlock Title)> strips = [];
    CapsuleQueueState? state;
    Border? pressedStrip;
    uint pressedPointer;
    bool dragging;
    double pressX, pressY;

    public event Action<Guid>? ExpandRequested;
    internal event Action<Guid>? EditRequested;
    public event Action<Guid>? HideRequested;
    public event Action<Guid>? DeleteRequested;
    public event Action? MasterToggleRequested;
    internal event Action<CapsuleDragResult>? DragCompleted;
    internal event Action<CapsuleDragResult>? DragMoved;
    internal event Action? DragCancelled;
    public event Action<double>? ScrollDelta;
    public event Action<Guid, int?>? HoverChanged;
    public event Action? NewNoteRequested;
    internal event Action? OpenManagerRequested, ShowAllRequested;

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
        CancelActiveDrag();
        Surface.Children.Clear();
        strips.Clear();
        var master = BuildMaster(next);
        Canvas.SetLeft(master, next.Edge == NoteEdge.Right ? NoteCapsuleGeometry.CapsuleWidth - master.Width : 0);
        Canvas.SetTop(master, 0);
        Canvas.SetZIndex(master, 1);
        Surface.Children.Add(master);
        if (next.Collapsed) return;
        var memberStart = NoteCapsuleGeometry.MasterLength(next.Edge, next.Members.Count) + NoteCapsuleGeometry.Gap;
        var index = 0;
        foreach (var member in next.Members)
        {
            var strip = BuildMember(member, next.Edge);
            var along = memberStart +
                index * NoteCapsuleGeometry.Step(next.Edge) - next.Scroll;
            Canvas.SetLeft(strip, next.Edge == NoteEdge.Top ? along : 0);
            Canvas.SetTop(strip, next.Edge == NoteEdge.Top ? 0 : along);
            var clipped = Math.Max(0, memberStart - along);
            if (clipped >= (next.Edge == NoteEdge.Top ? strip.Width : strip.Height)) strip.Visibility = Visibility.Collapsed;
            else if (clipped > 0)
                strip.Clip = new RectangleGeometry { Rect = next.Edge == NoteEdge.Top
                    ? new Rect(clipped, 0, strip.Width - clipped, strip.Height)
                    : new Rect(0, clipped, strip.Width, strip.Height - clipped) };
            Surface.Children.Add(strip);
            index++;
        }
    }

    // ---------------- 主胶囊 ----------------

    internal nint BuildHitRegion(float scale, double width, double height)
    {
        int Px(double value) => (int)Math.Round(value * scale);
        var region = Native.CreateRectRgn(0, 0, 0, 0);
        foreach (var pill in Surface.Children.OfType<Border>())
        {
            if (pill.Visibility == Visibility.Collapsed) continue;
            var x = Canvas.GetLeft(pill);
            var y = Canvas.GetTop(pill);
            var left = Px(x);
            var top = Px(y);
            var right = Px(x + pill.Width);
            var bottom = Px(y + pill.Height);
            // The binary GDI region must enclose XAML's antialiased curve, but stay inside the strip bounds.
            var shape = RoundedRegion(left - 1, top - 1, right + 1, bottom + 1, Px(EdgeRadius), pill.CornerRadius);
            var visible = pill.Clip is RectangleGeometry clip
                ? Native.CreateRectRgn(Px(x + clip.Rect.Left), Px(y + clip.Rect.Top), Px(x + clip.Rect.Right), Px(y + clip.Rect.Bottom))
                : Native.CreateRectRgn(left, top, right, bottom);
            Native.CombineRgn(shape, shape, visible, 1);
            Native.DeleteObject(visible);
            Native.CombineRgn(region, region, shape, 2);
            Native.DeleteObject(shape);
        }
        var bounds = Native.CreateRectRgn(0, 0, Px(width), Px(height));
        Native.CombineRgn(region, region, bounds, 1);
        Native.DeleteObject(bounds);
        return region;
    }

    const double EdgeRadius = NoteCapsuleGeometry.CapsuleHeight / 2;
    internal static CornerRadius EdgeCorners(NoteEdge edge) => new(edge == NoteEdge.Right ? EdgeRadius : 0,
        edge == NoteEdge.Left ? EdgeRadius : 0, edge != NoteEdge.Right ? EdgeRadius : 0, edge != NoteEdge.Left ? EdgeRadius : 0);

    internal static nint RoundedRegion(int left, int top, int right, int bottom, int radius, CornerRadius corners)
    {
        var shape = Native.CreateRoundRectRgn(left, top, right, bottom, radius * 2, radius * 2);
        var flat = corners.BottomLeft == 0 ? Native.CreateRectRgn(left, top, left + radius, bottom)
            : corners.BottomRight == 0 ? Native.CreateRectRgn(right - radius, top, right, bottom)
            : Native.CreateRectRgn(left, top, right, top + radius);
        Native.CombineRgn(shape, shape, flat, 2);
        Native.DeleteObject(flat);
        return shape;
    }

    Border BuildMaster(CapsuleQueueState queue)
    {
        var width = NoteCapsuleGeometry.MasterWidth(queue.Members.Count);
        var pill = new Border
        {
            Width = width,
            Height = NoteCapsuleGeometry.MasterHeight,
            Background = AccentProto.Foreground,
            CornerRadius = EdgeCorners(queue.Edge),
            Padding = new Thickness(10, 0, 10, 0),
        };
        var foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        var content = new Grid { ColumnSpacing = 6 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        content.Children.Add(new PaneIcon { Glyph = "note", Width = 14, Height = 14,
            VerticalAlignment = VerticalAlignment.Center, Foreground = foreground });
        var chevron = new PaneIcon { Glyph = "chev-r", Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = foreground, RenderTransformOrigin = new Point(.5, .5),
            RenderTransform = MasterChevronRotation(queue.Edge, queue.Collapsed) };
        Grid.SetColumn(chevron, 2);
        content.Children.Add(chevron);
        var count = new TextBlock { Text = queue.Members.Count.ToString(), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, FontFamily = (FontFamily)Application.Current.Resources["PaneFontMono"],
            FontSize = 12, Foreground = foreground };
        Grid.SetColumn(count, 1);
        content.Children.Add(count);
        pill.Child = content;
        pill.PointerEntered += (_, _) => content.Opacity = .82;
        pill.PointerExited += (_, _) => content.Opacity = 1;
        var toggleLabel = queue.Collapsed ? Strings.Notes_CapsulesExpand : Strings.Notes_CapsulesCollapse;
        AutomationProperties.SetName(pill, string.Format(Strings.Notes_CapsuleCount, (object?)queue.Members.Count) + "；" + toggleLabel);
        ToolTipService.SetToolTip(pill, toggleLabel);
        HookStrip(pill, Guid.Empty, master: true);
        pill.RightTapped += (_, args) =>
        {
            args.Handled = true;
            var menu = new MenuFlyout();
            Action? selectedAction = null;
            menu.Closed += (_, _) =>
            {
                if (selectedAction is { } action) DispatcherQueue.TryEnqueue(action.Invoke);
            };
            foreach (var (label, action) in new[] { (Strings.Notes_New, NewNoteRequested),
                (Strings.Notes_OpenManager, OpenManagerRequested), (Strings.Notes_ShowDesktopNotes, ShowAllRequested) })
            {
                var item = new MenuFlyoutItem { Text = label };
                item.Click += (_, _) => selectedAction = action;
                menu.Items.Add(item);
            }
            menu.ShowAt(pill, args.GetPosition(pill));
        };
        return pill;
    }

    /// <summary>图标表示点击后的动作：展开态给「收起」箭头，收起态给「展开」箭头。</summary>
    static RotateTransform MasterChevronRotation(NoteEdge edge, bool collapsed)
    {
        if (NoteCapsuleGeometry.IsVertical(edge)) return new RotateTransform { Angle = collapsed ? 90 : -90 };
        return new RotateTransform { Angle = collapsed ? 0 : 180 };
    }

    // ---------------- 成员胶囊 ----------------

    Border BuildMember(CapsuleMember member, NoteEdge edge)
    {
        var pill = new Border
        {
            Width = NoteCapsuleGeometry.CapsuleWidth,
            Height = NoteCapsuleGeometry.CapsuleHeight,
            Background = FillProto.Background,
            CornerRadius = EdgeCorners(edge),
            BorderBrush = AccentProto.Foreground,
            BorderThickness = new Thickness(member.Highlighted ? 2.5 : 2),
        };
        var content = new Grid { ColumnSpacing = 6, Margin = new Thickness(8, 0, 4, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (enabled, glyph, label) in new[] { (member.Topmost, "pin", Strings.Notes_Topmost),
            (member.DesktopPinned, "monitor", Strings.Notes_PinnedDesktop), (member.Reminder, "timer", Strings.Notes_Reminder) })
        {
            if (!enabled) continue;
            var badge = new PaneIcon { Glyph = glyph, Width = 12, Height = 12, IsFilled = glyph == "pin", Foreground = AccentProto.Foreground };
            AutomationProperties.SetName(badge, label);
            ToolTipService.SetToolTip(badge, label);
            badges.Children.Add(badge);
        }
        content.Children.Add(badges);
        var title = new TextBlock
        {
            Text = member.Title, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = TextProto.Foreground,
        };
        Grid.SetColumn(title, 1);
        content.Children.Add(title);
        var hide = new Button { Style = (Style)Application.Current.Resources["PaneIconButton"],
            Width = 24, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(12), IsTabStop = false,
            Content = new PaneIcon { Glyph = "close", Width = 10, Height = 10 } };
        AutomationProperties.SetName(hide, Strings.Notes_CapsuleHide);
        ToolTipService.SetToolTip(hide, Strings.Notes_CapsuleHide);
        hide.Click += (_, _) => HideRequested?.Invoke(member.Id);
        Grid.SetColumn(hide, 2);
        content.Children.Add(hide);
        var surface = new Grid();
        var press = new Border { Background = HoverProto.Foreground, CornerRadius = pill.CornerRadius,
            IsHitTestVisible = false, Opacity = 0 };
        surface.Children.Add(press);
        surface.Children.Add(content);
        pill.Child = surface;
        pill.PointerPressed += (_, _) => press.Opacity = 1;
        pill.PointerReleased += (_, _) => press.Opacity = 0;
        pill.PointerCaptureLost += (_, _) => press.Opacity = 0;
        var name = member.Title.Length == 0 ? Strings.Notes_Untitled : member.Title;
        title.Text = name;
        strips[member.Id] = (pill, title);
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
        expand.Click += (_, _) => EditRequested?.Invoke(id);
        menu.Items.Add(expand);
        var hide = new MenuFlyoutItem { Text = Strings.Notes_CapsuleHide };
        hide.Click += (_, _) => HideRequested?.Invoke(id);
        menu.Items.Add(hide);
        var delete = new MenuFlyoutItem { Text = Strings.Notes_DeleteCard };
        delete.Click += (_, _) => DeleteRequested?.Invoke(id);
        menu.Items.Add(delete);
        menu.ShowAt(strip, args.GetPosition(strip));
    }


    // ---------------- 指针交互：点击 / 拖动 / 悬停 / 滚轮 ----------------

    void HookStrip(Border pill, Guid id, bool master)
    {
        CapsuleDragResult Position(Point local, Native.Point screen) =>
            new(id, master, local.X - pressX, local.Y - pressY, screen.X, screen.Y);

        pill.PointerPressed += (_, args) =>
        {
            if (pressedStrip != null) return;
            var point = args.GetCurrentPoint(Surface);
            if (!point.Properties.IsLeftButtonPressed || !pill.CapturePointer(args.Pointer)) return;
            pressedStrip = pill;
            pressedPointer = args.Pointer.PointerId;
            pressX = point.Position.X;
            pressY = point.Position.Y;
            args.Handled = true;
        };
        pill.PointerMoved += (_, args) =>
        {
            if (!ReferenceEquals(pressedStrip, pill) || pressedPointer != args.Pointer.PointerId) return;
            var point = args.GetCurrentPoint(Surface);
            if (!point.Properties.IsLeftButtonPressed) return;
            var dx = point.Position.X - pressX;
            var dy = point.Position.Y - pressY;
            if (!dragging && dx * dx + dy * dy <= 25) return;
            dragging = true;
            if (Native.GetCursorPos(out var screen)) DragMoved?.Invoke(Position(point.Position, screen));
            args.Handled = true;
        };
        pill.PointerReleased += (_, args) =>
        {
            if (!ReferenceEquals(pressedStrip, pill) || pressedPointer != args.Pointer.PointerId) return;
            var point = args.GetCurrentPoint(Surface).Position;
            var wasDragging = dragging;
            pressedStrip = null;
            dragging = false;
            pill.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
            if (!wasDragging)
            {
                if (master) MasterToggleRequested?.Invoke();
                else ExpandRequested?.Invoke(id);
            }
            else if (Native.GetCursorPos(out var screen)) DragCompleted?.Invoke(Position(point, screen));
            else DragCancelled?.Invoke();
        };
        pill.PointerCaptureLost += (_, _) =>
        {
            if (ReferenceEquals(pressedStrip, pill)) CancelActiveDrag();
        };
        if (!master)
        {
            pill.PointerEntered += (_, _) =>
            {
                if (pressedStrip == null) HoverChanged?.Invoke(id, IndexOf(id));
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
        strips.TryGetValue(id, out var member) ? (Canvas.GetLeft(member.Strip), Canvas.GetTop(member.Strip)) : null;

    internal void UpdateMemberTitle(Guid id, string title)
    {
        var name = title.Length == 0 ? Strings.Notes_Untitled : title;
        if (!strips.TryGetValue(id, out var member) || member.Title.Text == name) return;
        member.Title.Text = name;
        AutomationProperties.SetName(member.Strip, name);
        ToolTipService.SetToolTip(member.Strip, name);
    }

    internal void CancelActiveDrag()
    {
        var strip = pressedStrip;
        if (strip == null) return;
        pressedStrip = null;
        dragging = false;
        strip.ReleasePointerCaptures();
        DragCancelled?.Invoke();
    }

    void WheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (pressedStrip != null || state is not { } queue || queue.Collapsed) return;
        var delta = args.GetCurrentPoint(Surface).Properties.MouseWheelDelta;
        if (delta == 0) return;
        // 顶部队列：普通滚轮转横向滚动；左右队列天然纵向。
        var effective = NoteCapsuleGeometry.IsVertical(queue.Edge) ? delta : -delta;
        ScrollDelta?.Invoke(effective / 40.0);
        args.Handled = true;
    }
}
