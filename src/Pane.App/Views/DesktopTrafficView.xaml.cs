using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Traffic;
using Pane.Settings;
using Windows.Foundation;

namespace Pane.Views;

public sealed partial class DesktopTrafficView : UserControl, IPanelView, IDisposable
{
    internal const double RankingWidth = 260;
    readonly InterfaceTrafficMonitor monitor;
    readonly ProcessTrafficMonitor process;
    bool observing, overHud, overRanking, menuOpen, dragging;
    internal Func<double>? RankingOffset { get; set; }
    internal Func<DesktopTrafficSettings?>? ReadOptions { get; set; }
    internal Action<bool, bool, bool>? SaveOptions { get; set; }
    internal Action? RevealEdge { get; set; }
    internal Action? PointerLeft { get; set; }
    internal bool PointerInside => overHud || overRanking || menuOpen || dragging;

    internal DesktopTrafficView(InterfaceTrafficMonitor monitor, ProcessTrafficMonitor process)
    {
        this.monitor = monitor;
        this.process = process;
        InitializeComponent();
        Actions.Closed += (_, _) => { menuOpen = false; PointerLeft?.Invoke(); };
    }

    public Size PreferredSize => new(110, 44);
    internal double CardRadius => RateCard.CornerRadius.TopLeft;
    public UIElement DragRegion => Surface;
    event EventHandler<Size>? IPanelView.SizeRequested { add { } remove { } }
    event EventHandler? IPanelView.CloseRequested { add { } remove { } }
    internal event EventHandler? OpenRequested;
    internal event EventHandler? UnpinRequested;

    public void OnShowing(PanelShowContext context) => Apply(monitor.Current);

    public void OnShown()
    {
        if (observing) return;
        observing = true;
        monitor.Subscribe(Apply);
    }

    public void OnHidden()
    {
        overHud = overRanking = dragging = menuOpen = false;
        Actions.Hide();
        SetRanks(false);
        if (!observing) return;
        observing = false;
        monitor.Unsubscribe(Apply);
    }

    internal void SetEdgeTab(bool visible)
    {
        RateCard.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        EdgeTab.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void StartDrag()
    {
        dragging = true;
        SetRanks(false);
    }

    internal void EndDrag(bool inside)
    {
        dragging = false;
        overHud = inside;
    }

    void HudEntered(object sender, PointerRoutedEventArgs args)
    {
        overHud = true;
        RevealEdge?.Invoke();
        if (observing && !dragging && !menuOpen && !args.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed)
            SetRanks(true);
    }

    void HudExited(object sender, PointerRoutedEventArgs args)
    {
        overHud = false;
        CloseRanksAfterPointerUpdate();
    }

    void RankingEntered(object sender, PointerRoutedEventArgs args) => overRanking = true;
    void RankingExited(object sender, PointerRoutedEventArgs args)
    {
        overRanking = false;
        CloseRanksAfterPointerUpdate();
    }

    void CloseRanksAfterPointerUpdate() => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        if (!overHud && !overRanking)
        {
            SetRanks(false);
            PointerLeft?.Invoke();
        }
    });

    void SetRanks(bool show)
    {
        if (RankingPopup.IsOpen == show) return;
        if (show)
        {
            RankingPopup.HorizontalOffset = RankingOffset?.Invoke() ?? ActualWidth;
            RankStatus.Visibility = Visibility.Visible;
            RankStatus.Text = Strings.Traffic_ProcessConnecting;
            RankingPopup.IsOpen = true;
            process.Subscribe(ApplyRanks);
        }
        else
        {
            RankingPopup.IsOpen = false;
            overRanking = false;
            process.Unsubscribe(ApplyRanks);
            DownloadRows.Children.Clear();
            UploadRows.Children.Clear();
            DownloadRank.Visibility = UploadRank.Visibility = Visibility.Collapsed;
        }
    }

    void ApplyRanks(ProcessTrafficSample download, ProcessTrafficSample upload)
    {
        Render(DownloadRows, download.Rows, row => row.Download, DownloadHeading.Foreground);
        Render(UploadRows, upload.Rows, row => row.Upload, UploadHeading.Foreground);
        DownloadRank.Visibility = DownloadRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UploadRank.Visibility = UploadRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var noRows = DownloadRows.Children.Count + UploadRows.Children.Count == 0;
        var downStatus = !noRows && download.State == ProcessTrafficState.Live ? "" : TrafficView.ProcessStatusText(download.State, noRows);
        var upStatus = !noRows && upload.State == ProcessTrafficState.Live ? "" : TrafficView.ProcessStatusText(upload.State, noRows);
        RankStatus.Text = downStatus == upStatus ? downStatus : string.Join(" / ", new[] { downStatus, upStatus }.Where(s => !string.IsNullOrEmpty(s)));
        RankStatus.Visibility = RankStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        void Render(StackPanel target, IReadOnlyList<ProcessTrafficRow> rows, Func<ProcessTrafficRow, double?> rate, Brush color)
        {
            int count = 0;
            foreach (var row in rows)
            {
                if (rate(row) is not > 0) continue;
                if (count == target.Children.Count) target.Children.Add(CreateRankRow(color));
                var cells = ((Grid)target.Children[count++]).Children;
                var png = row.Process.ImagePath is { Length: > 0 } path ? ((App)Application.Current).FileIcons.TryGet(path, false) : null;
                var image = (Image)cells[1];
                var source = Files.SystemIcons.Load(png);
                if (image.Source != source) image.Source = source;
                cells[0].Visibility = source == null ? Visibility.Visible : Visibility.Collapsed;
                ((TextBlock)cells[2]).Text = row.Process.Name;
                ((TextBlock)cells[3]).Text = TrafficView.FormatRate(rate(row));
            }
            while (target.Children.Count > count) target.Children.RemoveAt(count);
        }
    }

    Grid CreateRankRow(Brush color)
    {
        var row = new Grid { ColumnSpacing = 8, MinHeight = 20 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new Controls.PaneIcon { Glyph = "app", Width = 16, Height = 16, Foreground = RankStatus.Foreground });
        row.Children.Add(new Image { Width = 18, Height = 18 });
        var name = new TextBlock { FontSize = RankStatus.FontSize, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var rate = new TextBlock { Foreground = color, FontSize = RankStatus.FontSize, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(rate, 2);
        row.Children.Add(rate);
        return row;
    }

    public void Dispose() => OnHidden();

    void Apply(InterfaceTrafficReading reading)
    {
        Download.Text = TrafficView.FormatRate(reading.Rate?.Download);
        Upload.Text = TrafficView.FormatRate(reading.Rate?.Upload);
        AutomationProperties.SetHelpText(Surface, TrafficView.InterfaceStatusText(reading.State));
    }

    void ActionsOpening(object sender, object args)
    {
        SetRanks(false);
        menuOpen = true;
        var state = ReadOptions?.Invoke();
        TopmostItem.Visibility = EdgeItem.Visibility = FullscreenItem.Visibility = state == null ? Visibility.Collapsed : Visibility.Visible;
        TopmostItem.IsChecked = state?.Topmost == true;
        EdgeItem.IsChecked = state?.AutoHideAtEdge == true;
        FullscreenItem.IsChecked = state?.HideInFullscreen == true;
    }

    void OptionsClicked(object sender, RoutedEventArgs args) =>
        SaveOptions?.Invoke(TopmostItem.IsChecked, EdgeItem.IsChecked, FullscreenItem.IsChecked);

    void OpenTapped(object sender, TappedRoutedEventArgs args)
    {
        if (RevealEdge == null) OpenDetails();
    }
    void HudKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)) return;
        args.Handled = true;
        OpenClicked(sender, args);
    }

    void OpenClicked(object sender, RoutedEventArgs args) => OpenDetails();

    internal void OpenDetails()
    {
        if (RankingPopup.IsOpen)
        {
            // Let the desktop island finish closing the popup before activating another XAML window.
            RankingPopup.Closed += OpenAfterRankingClosed;
            SetRanks(false);
            return;
        }
        DispatcherQueue.TryEnqueue(() => OpenRequested?.Invoke(this, EventArgs.Empty));
    }

    void OpenAfterRankingClosed(object? sender, object args)
    {
        RankingPopup.Closed -= OpenAfterRankingClosed;
        DispatcherQueue.TryEnqueue(() => OpenRequested?.Invoke(this, EventArgs.Empty));
    }

    void UnpinClicked(object sender, RoutedEventArgs args) => UnpinRequested?.Invoke(this, EventArgs.Empty);
}
