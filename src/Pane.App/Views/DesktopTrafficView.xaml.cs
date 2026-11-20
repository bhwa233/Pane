using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Traffic;
using Windows.Foundation;

namespace Pane.Views;

public sealed partial class DesktopTrafficView : UserControl, IPanelView, IDisposable
{
    readonly InterfaceTrafficMonitor monitor;
    readonly ProcessTrafficMonitor process;
    bool observing, showingRanks;

    internal DesktopTrafficView(InterfaceTrafficMonitor monitor, ProcessTrafficMonitor process)
    {
        this.monitor = monitor;
        this.process = process;
        InitializeComponent();
        UpdateRankButton();
    }

    public Size PreferredSize => new(256, 124);
    public UIElement DragRegion => Header;
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
        if (!observing) return;
        observing = false;
        SetRanks(false);
        monitor.Unsubscribe(Apply);
    }

    void RanksClicked(object sender, RoutedEventArgs args)
    {
        if (observing) SetRanks(!showingRanks);
    }

    void SetRanks(bool show)
    {
        if (showingRanks == show) return;
        showingRanks = show;
        CardTitle.Text = show ? Strings.Traffic_DesktopRanks : Strings.Traffic_InterfaceTotal;
        InterfaceBody.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        RankBody.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateRankButton();
        if (show)
        {
            RankStatus.Text = Strings.Traffic_ProcessConnecting;
            process.Subscribe(ApplyRanks);
        }
        else
        {
            process.Unsubscribe(ApplyRanks);
            RankRows.Children.Clear();
        }
    }

    void UpdateRankButton()
    {
        RanksIcon.Glyph = showingRanks ? "globe" : "pulse";
        var label = showingRanks ? Strings.Traffic_DesktopShowInterface : Strings.Traffic_DesktopShowRanks;
        AutomationProperties.SetName(RanksButton, label);
        ToolTipService.SetToolTip(RanksButton, label);
    }

    void ApplyRanks(ProcessTrafficSample sample)
    {
        var count = 0;
        foreach (var row in sample.Rows)
        {
            if (row.Download is not > 0 && row.Upload is not > 0) continue;
            if (count == RankRows.Children.Count) RankRows.Children.Add(CreateRankRow());
            var cells = ((Grid)RankRows.Children[count]).Children;
            var name = (TextBlock)cells[0];
            var rates = (TextBlock)cells[1];
            if (name.Text != row.Process.Name) name.Text = row.Process.Name;
            var summary = $"\u2193 {TrafficView.FormatRate(row.Download)}  \u2191 {TrafficView.FormatRate(row.Upload)}";
            if (rates.Text != summary) rates.Text = summary;
            if (++count == ProcessTrafficMonitor.DesktopRankingLimit) break;
        }
        while (RankRows.Children.Count > count) RankRows.Children.RemoveAt(count);
        var status = TrafficView.ProcessStatusText(sample.State, count == 0);
        if (RankStatus.Text != status) RankStatus.Text = status;
    }

    Grid CreateRankRow()
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Foreground = CardTitle.Foreground, FontSize = RankStatus.FontSize, TextTrimming = TextTrimming.CharacterEllipsis });
        var rates = new TextBlock { Foreground = RankStatus.Foreground, FontSize = RankStatus.FontSize, TextAlignment = TextAlignment.Right };
        Grid.SetColumn(rates, 1);
        row.Children.Add(rates);
        return row;
    }

    public void Dispose() => OnHidden();

    void Apply(InterfaceTrafficReading reading)
    {
        Download.Text = TrafficView.FormatRate(reading.Rate?.Download);
        Upload.Text = TrafficView.FormatRate(reading.Rate?.Upload);
        Status.Text = TrafficView.InterfaceStatusText(reading.State);
    }

    void OpenClicked(object sender, RoutedEventArgs args) => OpenRequested?.Invoke(this, EventArgs.Empty);
    void UnpinClicked(object sender, RoutedEventArgs args) => UnpinRequested?.Invoke(this, EventArgs.Empty);
}
