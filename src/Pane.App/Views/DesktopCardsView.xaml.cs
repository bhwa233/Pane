using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
    readonly Action<string> openToolOutside;
    readonly Action<bool> setCardsVisible;
    readonly Func<string, Windows.Foundation.Size?> toolSize;
    readonly Func<IEnumerable<(string Key, string Icon, string Title)>> tools;
    readonly Func<Guid?> createNote;
    readonly Action openSettings;
    readonly Action<string> locateTool;
    readonly HashSet<string> activeTools = [];
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer clockTimer;
    bool visible;
    bool cardsVisible = true;

    internal DesktopCardsView(Func<IEnumerable<(string Key, string Title)>> catalog, Func<string, FrameworkElement?> create,
        Action<string, CardDesktopPlacement?> save, Action close, Action<string> openToolOutside, Action<bool> setCardsVisible,
        Func<string, Windows.Foundation.Size?> toolSize, Func<IEnumerable<(string Key, string Icon, string Title)>> tools,
        Func<Guid?> createNote, Action openSettings, Action<string> locateTool)
    {
        this.catalog = catalog;
        this.create = create;
        this.save = save;
        this.close = close;
        this.openToolOutside = openToolOutside;
        this.setCardsVisible = setCardsVisible;
        this.toolSize = toolSize;
        this.tools = tools;
        this.createNote = createNote;
        this.openSettings = openSettings;
        this.locateTool = locateTool;
        InitializeComponent();
        PaneLogo.Source = new SvgImageSource(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "pane-app.svg")));
        SetCardsVisible(true);
        clockTimer = DispatcherQueue.CreateTimer();
        clockTimer.Interval = TimeSpan.FromSeconds(1);
        clockTimer.Tick += (_, _) => UpdateClock();
    }

    internal void Apply(IReadOnlyDictionary<string, CardDesktopPlacement> layout)
    {
        var entries = catalog().ToDictionary(entry => entry.Key, entry => entry.Title);
        activeTools.Clear();
        foreach (var (key, _, _) in tools())
            if (layout.ContainsKey(key) && toolSize(key) != null) activeTools.Add(key);
        foreach (var key in cards.Keys.Where(key => !layout.ContainsKey(key) || !entries.ContainsKey(key)).ToArray())
            Remove(key);
        foreach (var (key, bounds) in layout)
        {
            if (activeTools.Contains(key) || !entries.ContainsKey(key)) continue;
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
            var frame = new Border { Child = content, Visibility = cardsVisible ? Visibility.Visible : Visibility.Collapsed };
            if (content is DesktopNoteView)
            {
                frame.CornerRadius = new(8);
                frame.Style = (Style)Application.Current.Resources["PaneCardSurface"];
            }
            cards.Add(key, (frame, content, bounds));
            Tiles.Children.Add(frame);
            WireDrag(key, frame, content);
            UpdateSurface(key, frame, content, bounds);
            Raise(frame);
            if (visible && cardsVisible && content is DesktopTrafficView hud) hud.OnShown();
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
                if (value && cardsVisible) hud.OnShown();
                else hud.OnHidden();
            }
    }

    internal void SetCardsVisible(bool value)
    {
        cardsVisible = value;
        var label = value ? Strings.CardDesktop_HideCards : Strings.CardDesktop_ShowCards;
        AutomationProperties.SetName(PaneButton, $"Pane · {label}");
        ToolTipService.SetToolTip(PaneButton, label);
        PaneLogo.Opacity = value ? 1 : 0.55;
        foreach (var (frame, content, _) in cards.Values)
        {
            frame.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (content is DesktopTrafficView hud && visible)
            {
                if (value) hud.OnShown();
                else hud.OnHidden();
            }
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
            var region = (content as IPanelView)?.DragRegion;
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

    bool IsAdded(string key) => activeTools.Contains(key) || cards.ContainsKey(key);

    void UpdatePanelTools(Dictionary<string, string> entries)
    {
        PanelTools.Children.Clear();
        foreach (var (tool, glyph, label) in tools())
        {
            bool multiple = tool.EndsWith(':');
            var matching = entries.Where(entry => multiple ? entry.Key.StartsWith(tool, StringComparison.Ordinal) : entry.Key == tool).ToArray();
            var icon = new PaneIcon { Glyph = glyph, Width = 16, Height = 16 };
            var button = new Button { Style = (Style)Application.Current.Resources["PaneIconButton"], IsTabStop = true, Content = icon };
            if (matching.Any(entry => IsAdded(entry.Key)))
                button.Content = new Border { Width = PaneTokens.ControlSm, Height = PaneTokens.ControlSm, CornerRadius = new(8),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 81, 89)), Child = icon };
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            PanelTools.Children.Add(button);
            if (!multiple)
            {
                var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
                menu.Opening += (_, _) =>
                {
                    menu.Items.Clear();
                    bool added = IsAdded(tool);
                    var action = new MenuFlyoutItem { Text = added ? Strings.CardDesktop_Locate : Strings.CardDesktop_Add };
                    action.Click += (_, _) => ShowCard(tool);
                    menu.Items.Add(action);
                    AddManagement(menu, tool);
                };
                button.Flyout = menu;
                continue;
            }

            var list = new StackPanel();
            var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
                Content = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            flyout.Opening += (_, _) =>
            {
                list.Children.Clear();
                foreach (var (key, title) in matching.Where(entry => tool != "screenshot:" || IsAdded(entry.Key))
                    .OrderByDescending(entry => IsAdded(entry.Key)))
                {
                    var row = new Grid { ColumnSpacing = 4 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var added = IsAdded(key);
                    var select = new Button { Content = added ? title : $"+  {title}", HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        BorderThickness = new(0) };
                    ToolTipService.SetToolTip(select, added ? Strings.CardDesktop_Locate : Strings.CardDesktop_Add);
                    select.Click += (_, _) => { flyout.Hide(); ShowCard(key); };
                    row.Children.Add(select);
                    var actions = new MenuFlyout();
                    AddManagement(actions, key, flyout.Hide);
                    var more = new Button { Style = (Style)Application.Current.Resources["PaneIconButton"], IsTabStop = true,
                        Content = new TextBlock { Text = "⋯" }, Flyout = actions };
                    var name = string.Format(Strings.Launcher_MenuTarget, Strings.Launcher_MoreActions, title);
                    AutomationProperties.SetName(more, name);
                    ToolTipService.SetToolTip(more, Strings.Launcher_MoreActions);
                    Grid.SetColumn(more, 1);
                    row.Children.Add(more);
                    list.Children.Add(row);
                }
                var open = new Button { Content = Strings.CardDesktop_OpenWindow, HorizontalAlignment = HorizontalAlignment.Stretch };
                open.Click += (_, _) => { flyout.Hide(); openToolOutside(tool); };
                list.Children.Add(open);
            };
            button.Flyout = flyout;
        }
    }

    void AddManagement(MenuFlyout menu, string key, Action? dismiss = null)
    {
        var external = new MenuFlyoutItem { Text = Strings.CardDesktop_OpenWindow };
        external.Click += (_, _) =>
        {
            dismiss?.Invoke();
            openToolOutside(key);
        };
        menu.Items.Add(external);
        if (!IsAdded(key)) return;
        var remove = new MenuFlyoutItem { Text = Strings.CardDesktop_RemoveCard };
        remove.Click += (_, _) => { dismiss?.Invoke(); save(key, null); };
        menu.Items.Add(remove);
    }

    void ShowCard(string key)
    {
        if (!IsAdded(key)) AddCard(key);
        setCardsVisible(true);
        if (activeTools.Contains(key)) locateTool(key);
        else if (cards.TryGetValue(key, out var card)) Raise(card.Frame);
    }

    void AddOpening(object sender, object args)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();
        if (tools().Any(tool => tool.Key == "note:"))
        {
            var note = new MenuFlyoutItem { Text = Strings.Notes_New };
            note.Click += (_, _) =>
            {
                if (createNote() is not { } id) return;
                var key = $"note:{id:D}";
                ShowCard(key);
                if (cards.TryGetValue(key, out var card) && card.Content is DesktopNoteView view)
                    DispatcherQueue.TryEnqueue(view.FocusBody);
            };
            menu.Items.Add(note);
        }
        foreach (var (key, title) in catalog().Where(entry => !entry.Key.StartsWith("screenshot:", StringComparison.Ordinal) && !IsAdded(entry.Key)))
        {
            var entry = new MenuFlyoutItem { Text = title };
            entry.Click += (_, _) => ShowCard(key);
            menu.Items.Add(entry);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = Strings.CardDesktop_NoCardsToAdd, IsEnabled = false });
    }

    void AddCard(string key)
    {
        var step = (cards.Count + activeTools.Count) * 24;
        var size = toolSize(key) ?? (key == "hud" ? new Windows.Foundation.Size(126, 78) : new Windows.Foundation.Size(256, 206));
        save(key, new(step + 16, step + 16, size.Width, size.Height));
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

    void ToggleCardsClicked(object sender, RoutedEventArgs args) => setCardsVisible(!cardsVisible);
    void CloseClicked(object sender, RoutedEventArgs args) => close();
    void SettingsClicked(object sender, RoutedEventArgs args) => openSettings();
}
