using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Hosting;
using Pane.Localization;
using Pane.Settings;
using Pane.Views.Controls;
using Windows.System;

namespace Pane.Views;

public sealed partial class DesktopCardsView : UserControl, IDisposable
{
    readonly Dictionary<string, (Border Frame, FrameworkElement Content, CardDesktopPlacement Bounds)> cards = [];
    readonly Func<IEnumerable<(string Key, string Title)>> catalog;
    readonly Func<string, FrameworkElement?> create;
    readonly Action<string, CardDesktopPlacement?> save;
    readonly Action close;
    readonly Action openTrafficOutside;
    readonly Action openSettings;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer clockTimer;
    bool visible;

    internal DesktopCardsView(Func<IEnumerable<(string Key, string Title)>> catalog, Func<string, FrameworkElement?> create,
        Action<string, CardDesktopPlacement?> save, Action close, Action openTrafficOutside, Action openSettings)
    {
        this.catalog = catalog;
        this.create = create;
        this.save = save;
        this.close = close;
        this.openTrafficOutside = openTrafficOutside;
        this.openSettings = openSettings;
        InitializeComponent();
        clockTimer = DispatcherQueue.CreateTimer();
        clockTimer.Interval = TimeSpan.FromSeconds(1);
        clockTimer.Tick += (_, _) => UpdateClock();
    }

    internal void Apply(IReadOnlyDictionary<string, CardDesktopPlacement> layout)
    {
        var entries = catalog().ToDictionary(entry => entry.Key, entry => entry.Title);
        foreach (var key in cards.Keys.Where(key => !layout.ContainsKey(key) || !entries.ContainsKey(key)).ToArray())
            Remove(key);
        foreach (var (key, bounds) in layout)
        {
            if (!entries.ContainsKey(key)) continue;
            if (cards.TryGetValue(key, out var current))
            {
                if (current.Bounds.Equals(bounds)) continue;
                cards[key] = (current.Frame, current.Content, bounds);
                UpdateSurface(key, current.Frame, current.Content, bounds);
                Raise(current.Frame);
                continue;
            }
            if (create(key) is not { } content) continue;
            // The border supplies the card surface only; all controls belong to the plugin.
            var frame = new Border { Child = content };
            if (content is DesktopNoteView || key == "traffic")
            {
                frame.CornerRadius = new(8);
                frame.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
            }
            cards.Add(key, (frame, content, bounds));
            Tiles.Children.Add(frame);
            WireDrag(key, frame, content);
            UpdateSurface(key, frame, content, bounds);
            Raise(frame);
            if (visible && content is DesktopTrafficView hud) hud.OnShown();
        }
        UpdatePanelTools(entries);
    }

    internal void SetVisible(bool value)
    {
        visible = value;
        if (value) { UpdateClock(); clockTimer.Start(); }
        else clockTimer.Stop();
        foreach (var (_, content, _) in cards.Values)
            if (content is DesktopTrafficView hud)
            {
                if (value) hud.OnShown();
                else hud.OnHidden();
            }
    }

    internal void FocusEntry() => CloseButton.Focus(FocusState.Programmatic);

    void UpdateSurface(string key, Border frame, FrameworkElement content, CardDesktopPlacement bounds)
    {
        if (content is DesktopNoteView note)
        {
            note.RefreshWorkspaceTopmost();
            if (!bounds.Folded) frame.Child = note;
            else
            {
                var capsule = new Button
                {
                    Content = new TextBlock { Text = note.DisplayTitle, TextTrimming = TextTrimming.CharacterEllipsis },
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                capsule.Click += (_, _) =>
                {
                    var saved = cards[key].Bounds;
                    save(key, saved with { Folded = false });
                };
                frame.Child = capsule;
            }
        }
        Place(frame, content, bounds);
    }

    void Place(Border frame, FrameworkElement content, CardDesktopPlacement bounds)
    {
        frame.Width = content is DesktopTrafficView hud ? hud.PreferredSize.Width : bounds.Width;
        frame.Height = content is DesktopTrafficView trafficHud ? trafficHud.PreferredSize.Height : bounds.Height;
        if (content is DesktopNoteView && bounds.Folded)
        {
            frame.Width = NoteCapsuleGeometry.CapsuleWidth;
            frame.Height = NoteCapsuleGeometry.CapsuleHeight;
        }
        Canvas.SetLeft(frame, Math.Clamp(bounds.X, 0, Math.Max(0, Tiles.ActualWidth - frame.Width)));
        Canvas.SetTop(frame, Math.Clamp(bounds.Y, 0, Math.Max(0, Tiles.ActualHeight - 32)));
    }

    void WireDrag(string key, Border frame, FrameworkElement content)
    {
        Pointer? pointer = null;
        Windows.Foundation.Point start = default;
        CardDesktopPlacement origin = default!;
        bool moving = false;

        frame.AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (args.GetCurrentPoint(Tiles).Properties.IsLeftButtonPressed) Raise(frame);
            var region = content is IPanelView panel ? panel.DragRegion
                : (content as ContentControl)?.Content is TabView tabs ? tabs.TabStripFooter as UIElement : null;
            var source = args.OriginalSource as DependencyObject;
            bool canDrag = content is DesktopNoteView note ? note.CanStartWorkspaceDrag(source) : GlassPanel.CanDragFrom(region, source);
            if (pointer != null || !args.GetCurrentPoint(Tiles).Properties.IsLeftButtonPressed || !canDrag) return;
            pointer = args.Pointer;
            start = args.GetCurrentPoint(Tiles).Position;
            origin = cards[key].Bounds with { X = Canvas.GetLeft(frame), Y = Canvas.GetTop(frame) };
        }), true);
        frame.AddHandler(PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            if (pointer?.PointerId != args.Pointer.PointerId) return;
            var point = args.GetCurrentPoint(Tiles);
            var x = point.Position.X - start.X;
            var y = point.Position.Y - start.Y;
            if (!moving)
            {
                if (!point.Properties.IsLeftButtonPressed) { pointer = null; return; }
                var scale = XamlRoot.RasterizationScale;
                if (!GlassPanel.PassedDragThreshold(x * scale, y * scale) || !frame.CapturePointer(args.Pointer)) return;
                moving = true;
                if (content is DesktopTrafficView hud) hud.StartDrag();
            }
            Place(frame, content, origin with { X = origin.X + x, Y = origin.Y + y });
            args.Handled = true;
        }), content is DesktopNoteView);
        frame.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, args) =>
        {
            if (pointer?.PointerId != args.Pointer.PointerId) return;
            pointer = null;
            if (!moving) return;
            moving = false;
            if (content is DesktopTrafficView hud) hud.EndDrag(true);
            var saved = origin with { X = Canvas.GetLeft(frame), Y = Canvas.GetTop(frame) };
            cards[key] = (frame, content, saved);
            frame.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
            save(key, saved);
        }), content is DesktopNoteView);
        frame.PointerExited += (_, _) => { if (!moving) pointer = null; };
        frame.PointerCaptureLost += (_, args) =>
        {
            if (pointer?.PointerId != args.Pointer.PointerId) return;
            pointer = null;
            if (!moving) return;
            moving = false;
            if (content is DesktopTrafficView hud) hud.EndDrag(false);
            Place(frame, content, cards[key].Bounds);
        };
    }

    void Raise(Border frame)
    {
        var ordered = cards.Values.OrderBy(card => card.Bounds.Topmost)
            .ThenBy(card => card.Frame == frame).ThenBy(card => Canvas.GetZIndex(card.Frame)).ToArray();
        int z = 0;
        foreach (var item in ordered) Canvas.SetZIndex(item.Frame, z++);
    }

    void CanvasSizeChanged(object sender, SizeChangedEventArgs args)
    {
        foreach (var (frame, content, bounds) in cards.Values) Place(frame, content, bounds);
        Tiles.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = new(0, 0, args.NewSize.Width, args.NewSize.Height) };
    }

    internal void Remove(string key)
    {
        if (!cards.Remove(key, out var card)) return;
        (card.Content as IDisposable)?.Dispose();
        Tiles.Children.Remove(card.Frame);
    }

    internal void UpdateScreenshot(Guid id, string path)
    {
        if (cards.TryGetValue($"screenshot:{id:D}", out var card)) ((DesktopScreenshotView)card.Content).Apply(path);
    }

    public void Dispose()
    {
        clockTimer.Stop();
        foreach (var key in cards.Keys.ToArray()) Remove(key);
    }

    void UpdateClock() => Clock.Text = DateTime.Now.ToString("HH:mm");

    void UpdatePanelTools(Dictionary<string, string> entries)
    {
        PanelTools.Children.Clear();
        AddTool("note:", "pencil", Strings.Module_Notes_Name);
        AddTool("screenshot:", "image", Strings.Module_Screenshots_Name);
        AddTool("hud", "pulse", Strings.Traffic_DesktopCard);
        AddTool("traffic", "globe", Strings.Module_Traffic_Name);

        void AddTool(string prefix, string glyph, string label)
        {
            var matching = entries.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (matching.Length == 0) return;
            bool active = matching.Any(entry => cards.ContainsKey(entry.Key));
            var button = new Button { Style = (Style)Application.Current.Resources["PaneIconButton"],
                Content = new PaneIcon { Glyph = glyph, Width = 16, Height = 16 } };
            if (active) button.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 81, 89));
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            var menu = new MenuFlyout();
            foreach (var (key, title) in matching.Where(entry => cards.ContainsKey(entry.Key)))
            {
                var item = new MenuFlyoutSubItem { Text = title };
                var locate = new MenuFlyoutItem { Text = Strings.CardDesktop_Locate };
                locate.Click += (_, _) => Raise(cards[key].Frame);
                item.Items.Add(locate);
                if (key == "traffic" || cards[key].Content is DesktopScreenshotView)
                {
                    var external = new MenuFlyoutItem { Text = Strings.CardDesktop_OpenWindow };
                    external.Click += (_, _) =>
                    {
                        if (key == "traffic") openTrafficOutside();
                        else ((DesktopScreenshotView)cards[key].Content).OpenImage();
                    };
                    item.Items.Add(external);
                }
                var remove = new MenuFlyoutItem { Text = Strings.Common_Remove };
                remove.Click += (_, _) => save(key, null);
                item.Items.Add(remove);
                menu.Items.Add(item);
            }
            button.IsEnabled = active;
            button.Flyout = menu;
            PanelTools.Children.Add(button);
        }
    }

    void AddOpening(object sender, object args)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();
        foreach (var (key, title) in catalog().Where(entry => !cards.ContainsKey(entry.Key)))
        {
            var entry = new MenuFlyoutItem { Text = title };
            entry.Click += (_, _) =>
            {
                var step = cards.Count * 24;
                var size = key == "traffic" ? TrafficHistoryView.PreferredSize :
                    key == "hud" ? new Windows.Foundation.Size(126, 78) : new Windows.Foundation.Size(256, 206);
                save(key, new(step + 16, step + 16, size.Width, size.Height));
            };
            menu.Items.Add(entry);
        }
    }

    void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        args.Handled = true;
        for (var node = args.OriginalSource as DependencyObject; node != null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (node is DesktopNoteView note && note.FinishEditing())
            {
                note.Focus(FocusState.Programmatic);
                return;
            }
        close();
    }

    void CanvasTapped(object sender, TappedRoutedEventArgs args)
    {
        if (ReferenceEquals(args.OriginalSource, Tiles)) close();
    }

    void CloseClicked(object sender, RoutedEventArgs args) => close();
    void SettingsClicked(object sender, RoutedEventArgs args) => openSettings();
}
