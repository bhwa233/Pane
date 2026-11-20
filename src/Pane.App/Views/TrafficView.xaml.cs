using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Launcher;
using Pane.Services.Traffic;
using Pane.Views.Controls;
using Windows.Foundation;
using Windows.System;

namespace Pane.Views;

public sealed partial class TrafficView : UserControl, ISecondaryView, IDisposable
{
    const string RateMode = "rate", AppRunMode = "app-run";
    sealed record DisplayRow(string Key, TrafficProcessIdentity Process, string Download, string Upload, bool HasGap, TrafficAppGroup? Group)
    {
        internal bool Matches(DisplayRow other) => Key == other.Key && Process == other.Process && Download == other.Download &&
            Upload == other.Upload && HasGap == other.HasGap && ((Group, other.Group) switch
            {
                (null, null) => true,
                ({ } group, { } previous) => group.MemberCount == previous.MemberCount && group.Members.SequenceEqual(previous.Members),
                _ => false
            });
    }

    readonly InterfaceTrafficMonitor interfaceTraffic;
    readonly ProcessTrafficMonitor processTraffic;
    readonly Action togglePin;
    readonly Action<TrafficProcessIdentity?> showHistory;
    readonly Action<TrafficProcessIdentity> showConnections;
    readonly PaneSwitch interfaceSwitch;
    readonly PaneSwitch processSwitch;
    readonly PaneSegmented processMode;
    bool shown, pointerInRanking, processMenuOpen, endingProcess;
    int adapterRequest, fileActionRequest;
    TrafficProcessIdentity? pendingEnd;
    ProcessTrafficState rankingState;
    TrafficRankingQuery? activeRanking;
    IReadOnlyList<ProcessTrafficRow> processRows = [];
    DisplayRow[] displayedRows = [];

    internal TrafficView(InterfaceTrafficMonitor interfaceTraffic, ProcessTrafficMonitor processTraffic,
        Action<bool> setInterfacePaused, Action togglePin, Action<TrafficProcessIdentity?> showHistory, Action<TrafficProcessIdentity> showConnections)
    {
        this.interfaceTraffic = interfaceTraffic;
        this.processTraffic = processTraffic;
        this.togglePin = togglePin;
        this.showHistory = showHistory;
        this.showConnections = showConnections;
        InitializeComponent();
        DownloadPlot.SizeChanged += (_, _) => DrawInterfaceTrend();
        UploadPlot.SizeChanged += (_, _) => DrawInterfaceTrend();
        var close = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = VirtualKey.W,
            Modifiers = VirtualKeyModifiers.Control
        };
        close.Invoked += (_, args) => { args.Handled = true; CloseRequested?.Invoke(this, EventArgs.Empty); };
        KeyboardAccelerators.Add(close);

        interfaceSwitch = new PaneSwitch(!interfaceTraffic.Paused, TogglePrototype.Background, TogglePrototype.BorderBrush, AccentPrototype.Background);
        AutomationProperties.SetName(interfaceSwitch, Strings.Traffic_InterfaceMonitoring);
        interfaceSwitch.Toggled += (_, value) =>
        {
            if (!shown) return;
            setInterfacePaused(!value);
            interfaceSwitch.IsOn = !interfaceTraffic.Paused;
        };
        InterfaceSwitchHost.Child = interfaceSwitch;
        processSwitch = new PaneSwitch(false, TogglePrototype.Background, TogglePrototype.BorderBrush, AccentPrototype.Background)
        {
            IsEnabled = false
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(processSwitch, Strings.Traffic_ProcessTitle);
        processSwitch.Toggled += (_, value) =>
        {
            if (!shown) return;
            processSwitch.IsEnabled = false;
            processTraffic.SetEnabled(value);
        };
        ProcessSwitchHost.Child = processSwitch;
        processMode = new PaneSegmented([(RateMode, Strings.Traffic_Live), (AppRunMode, Strings.Traffic_AppRun)], RateMode,
            ProcessStatus, ProcessStatus, TogglePrototype.Background, TogglePrototype.BorderBrush) { IsEnabled = false };
        AutomationProperties.SetName(processMode, Strings.Traffic_ProcessDisplayMode);
        processMode.Changed += (_, _) => ChangeRanking();
        ProcessFilter.MaxLength = TrafficRankingQuery.KeywordLimit;
        ProcessModeHost.Child = processMode;
    }

    public Size PreferredSize => new(480, 480);
    public Size MinimumSize => new(360, 360);
    public UIElement DragRegion => Header;
    event EventHandler<Size>? IPanelView.SizeRequested { add { } remove { } }
    event EventHandler<bool>? ISecondaryView.TopmostChanged { add { } remove { } }
    public event EventHandler? CloseRequested;

    public void OnShowing(PanelShowContext context)
    {
        ApplyInterfaceReading(interfaceTraffic.Current);
        ProcessStatus.Text = Strings.Traffic_ProcessConnecting;
        ProcessRetry.Visibility = Visibility.Collapsed;
        processSwitch.IsEnabled = false;
        processMode.IsEnabled = false;
        pointerInRanking = false;
        processRows = [];
        rankingState = ProcessTrafficState.Sampling;
        RenderProcessRows();
    }

    public void OnShown()
    {
        if (shown) return;
        shown = true;
        interfaceTraffic.Subscribe(ApplyInterfaceReading);
        activeRanking = ReadRankingQuery();
        processTraffic.SubscribeDetail(ApplyProcessSample, activeRanking);
    }

    public void OnHidden()
    {
        if (!shown) return;
        shown = false;
        fileActionRequest++;
        ClearEndConfirmation(false);
        AdapterFlyout.Hide();
        interfaceTraffic.Unsubscribe(ApplyInterfaceReading);
        processTraffic.UnsubscribeDetail();
    }

    public void Dispose() => OnHidden();

    async void AdapterFlyoutOpening(object sender, object args)
    {
        var request = ++adapterRequest;
        AdapterRows.Children.Clear();
        AdapterStatus.Text = Strings.Traffic_InterfaceReading;
        AdapterStatus.Visibility = Visibility.Visible;
        var adapters = await Task.Run(InterfaceTrafficSampler.ReadAdapters);
        if (!shown || request != adapterRequest) return;

        if (adapters.Length == 0)
        {
            AdapterStatus.Text = Strings.Traffic_InterfaceUnavailable;
            return;
        }

        AdapterStatus.Visibility = Visibility.Collapsed;
        foreach (var adapter in adapters)
        {
            var index = string.Format(Strings.Traffic_InterfaceIndex, adapter.Index);
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(adapter.Name) ? index : $"{adapter.Name} · {index}",
                FontSize = ProcessTitle.FontSize, Foreground = ProcessTitle.Foreground, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = adapter.Addresses.Count == 0 ? Strings.Traffic_InterfaceNoAddress : string.Join("\n", adapter.Addresses),
                FontSize = Status.FontSize, Foreground = Status.Foreground, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            AdapterRows.Children.Add(row);
        }
    }

    void AdapterFlyoutClosed(object sender, object args)
    {
        adapterRequest++;
        AdapterRows.Children.Clear();
    }

    void ApplyInterfaceReading(InterfaceTrafficReading reading)
    {
        Download.Text = FormatRate(reading.Rate?.Download);
        Upload.Text = FormatRate(reading.Rate?.Upload);
        Status.Text = InterfaceStatusText(reading.State);
        DrawInterfaceTrend();
    }

    internal static string InterfaceStatusText(InterfaceTrafficState state) => state switch
    {
        InterfaceTrafficState.Live => Strings.Traffic_Live,
        InterfaceTrafficState.Paused => Strings.Traffic_InterfacePaused,
        InterfaceTrafficState.Unavailable => Strings.Traffic_InterfaceUnavailable,
        _ => Strings.Traffic_Sampling
    };

    void DrawInterfaceTrend()
    {
        var trend = interfaceTraffic.Trend;
        var points = trend.Points;
        var download = new PointCollection();
        var upload = new PointCollection();
        string caption = Strings.Traffic_TrendWindow;
        if (points.Count > 1 && DownloadPlot.ActualWidth > 4 && UploadPlot.ActualWidth > 4)
        {
            double peak = 0;
            foreach (var point in points)
                peak = Math.Max(peak, Math.Max(point.Rate.Download, point.Rate.Upload));
            double scale = Math.Max(peak, 1);
            foreach (var point in points)
            {
                double age = Stopwatch.GetElapsedTime(point.Timestamp, trend.LatestTimestamp).TotalSeconds;
                double fraction = Math.Clamp(1 - age / InterfaceTrafficTrend.WindowSeconds, 0, 1);
                download.Add(PlotPoint(DownloadPlot, fraction, point.Rate.Download / scale));
                upload.Add(PlotPoint(UploadPlot, fraction, point.Rate.Upload / scale));
            }
            caption = string.Format(Strings.Traffic_TrendPeak, FormatRate(peak));
        }
        DownloadLine.Points = download;
        UploadLine.Points = upload;
        TrendCaption.Text = caption;
    }

    static Point PlotPoint(Canvas plot, double xFraction, double yFraction) =>
        new(2 + xFraction * (plot.ActualWidth - 4), 2 + (1 - yFraction) * Math.Max(plot.ActualHeight - 4, 0));

    internal void RefreshPin(bool pinned)
    {
        PinIcon.Glyph = pinned ? "pin-off" : "pin";
        var label = pinned ? Strings.Notes_UnpinDesktop : Strings.Notes_PinDesktop;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PinButton, label);
        ToolTipService.SetToolTip(PinButton, label);
    }

    void PinClicked(object sender, RoutedEventArgs args) => togglePin();

    void HistoryClicked(object sender, RoutedEventArgs args) => showHistory(null);

    void ProcessRetryClicked(object sender, RoutedEventArgs args)
    {
        ProcessRetry.IsEnabled = false;
        ProcessStatus.Text = Strings.Traffic_ProcessConnecting;
        processTraffic.Retry();
    }

    void ApplyProcessSample(ProcessTrafficSample sample)
    {
        processSwitch.IsOn = sample.Enabled ?? false;
        processSwitch.IsEnabled = sample.Enabled != null;
        rankingState = sample.State;
        var status = ProcessStatusText(sample.State, sample.Rows.Count == 0 && activeRanking?.HasFilter != true);
        if (sample.SettingsWriteFailed) status += "\n" + Strings.Traffic_ProcessSettingWriteFailed;
        if (ProcessStatus.Text != status) ProcessStatus.Text = status;
        processRows = sample.Rows;
        processMode.IsEnabled = sample.Enabled == true && (sample.State is ProcessTrafficState.Live or ProcessTrafficState.Partial || activeRanking != null);
        ProcessActiveOnly.IsEnabled = sample.Enabled == true;
        ProcessRetry.Visibility = sample.Enabled == null ? Visibility.Visible : Visibility.Collapsed;
        ProcessRetry.IsEnabled = sample.Enabled == null;
        RenderProcessRows(preserveOrder: pointerInRanking || processMenuOpen || HasRankingFocus());
    }

    internal static string ProcessStatusText(ProcessTrafficState state, bool empty) => state switch
    {
        ProcessTrafficState.NotInstalled => Strings.Traffic_ProcessNotInstalled,
        ProcessTrafficState.AccessDenied => Strings.Traffic_ProcessAccessDenied,
        ProcessTrafficState.PermissionDenied => Strings.Traffic_ProcessPermissionDenied,
        ProcessTrafficState.Disabled => Strings.Traffic_ProcessDisabled,
        ProcessTrafficState.Sampling => Strings.Traffic_Sampling,
        ProcessTrafficState.Partial => Strings.Traffic_ProcessPartial,
        ProcessTrafficState.Live => empty ? Strings.Traffic_ProcessEmpty : Strings.Traffic_Live,
        _ => Strings.Traffic_ProcessUnavailable
    };

    void ProcessFilterChanged(object sender, TextChangedEventArgs args) => ChangeRanking();

    void ProcessSortChanged(object sender, SelectionChangedEventArgs args) => ChangeRanking();

    void ProcessActiveOnlyChanged(object sender, RoutedEventArgs args) => ChangeRanking();

    TrafficRankingQuery? ReadRankingQuery()
    {
        var cumulative = processMode.Value == AppRunMode;
        var keyword = ProcessFilter.Text.Trim();
        var sort = ProcessSort.SelectedIndex switch
        {
            1 => TrafficRankingSort.Download,
            2 => TrafficRankingSort.Upload,
            _ => TrafficRankingSort.Total
        };
        var query = new TrafficRankingQuery(keyword, cumulative, sort, ProcessActiveOnly.IsChecked == true);
        return !cumulative && sort == TrafficRankingSort.Total && !query.HasFilter ? null : query;
    }

    void ChangeRanking()
    {
        if (!shown || processMode == null) return;
        var query = ReadRankingQuery();
        if (activeRanking == query) return;
        activeRanking = query;
        rankingState = ProcessTrafficState.Sampling;
        processRows = [];
        ProcessStatus.Text = Strings.Traffic_Sampling;
        RenderProcessRows();
        processTraffic.SetDetailQuery(query);
    }

    void RankingPointerEntered(object sender, PointerRoutedEventArgs args) => pointerInRanking = true;

    void RankingPointerExited(object sender, PointerRoutedEventArgs args)
    {
        pointerInRanking = false;
        if (shown && !processMenuOpen && !HasRankingFocus()) RenderProcessRows();
    }

    bool HasRankingFocus()
    {
        if (XamlRoot is not { } root || ProcessTable.Visibility != Visibility.Visible) return false;
        for (var focused = FocusManager.GetFocusedElement(root) as DependencyObject; focused != null; focused = VisualTreeHelper.GetParent(focused))
            if (ReferenceEquals(focused, ProcessTable)) return true;
        return false;
    }

    void RenderProcessRows(bool preserveOrder = false)
    {
        if (processMode is null) return;
        var cumulative = processMode.Value == AppRunMode;
        var matches = processRows.Where(row => cumulative || row.Download != null || row.Upload != null);
        if (preserveOrder && displayedRows.Length != 0)
            matches = TrafficRankingOrder.Preserve(matches, displayedRows.Select(row => row.Key));
        var rows = matches.Select(row => cumulative
            ? new DisplayRow(row.Key, row.Process, FormatRunBytes(row.AppRun?.Download, row.AppRun?.HasGap == true),
                FormatRunBytes(row.AppRun?.Upload, row.AppRun?.HasGap == true), row.AppRun?.HasGap == true, row.AppGroup)
            : new DisplayRow(row.Key, row.Process, FormatRate(row.Download), FormatRate(row.Upload), false, null)).ToArray();
        var noMatches = activeRanking?.HasFilter == true && rows.Length == 0 && rankingState == ProcessTrafficState.Live
            ? Visibility.Visible : Visibility.Collapsed;
        if (ProcessNoMatches.Visibility != noMatches) ProcessNoMatches.Visibility = noMatches;
        UpdateRows(rows);
    }

    static string FormatRunBytes(long? value, bool hasGap) => value is { } bytes
        ? (hasGap ? "≥ " : "") + TrafficHistoryView.FormatBytes(bytes) : "—";

    void UpdateRows(DisplayRow[] rows)
    {
        if (displayedRows.Length == rows.Length && displayedRows.Zip(rows).All(pair => pair.First.Matches(pair.Second))) return;
        var previous = new Dictionary<string, (Grid View, DisplayRow Row)>(StringComparer.Ordinal);
        for (var index = 0; index < displayedRows.Length; index++)
            previous.Add(displayedRows[index].Key, ((Grid)ProcessRows.Children[index], displayedRows[index]));
        var visible = rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        for (var index = displayedRows.Length - 1; index >= 0; index--)
            if (!visible.Contains(displayedRows[index].Key)) ProcessRows.Children.RemoveAt(index);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var existing = previous.TryGetValue(row.Key, out var entry);
            var view = existing ? entry.View : CreateRow();
            if (index == ProcessRows.Children.Count || !ReferenceEquals(ProcessRows.Children[index], view))
            {
                if (existing) ProcessRows.Children.Remove(view);
                ProcessRows.Children.Insert(index, view);
            }
            if (existing && entry.Row.Matches(row)) continue;
            var columns = view.Children;
            var groupLabel = row.Group is { MemberCount: > 1 } group ? string.Format(Strings.Traffic_RankingGroupName, row.Process.Name, group.MemberCount) : null;
            ((TextBlock)columns[0]).Text = groupLabel ?? row.Process.Name;
            ToolTipService.SetToolTip(columns[0], groupLabel ?? $"{row.Process.Name} (PID {row.Process.Pid})");
            SetProcessAction((Button)columns[3], row, Strings.Traffic_HistoryOpenProcess, Strings.Traffic_RankingPickHistory);
            var more = (Button)columns[4];
            more.Tag = row;
            var moreLabel = string.Format(Strings.Launcher_MenuTarget, Strings.Launcher_MoreActions, groupLabel ?? $"{row.Process.Name} (PID {row.Process.Pid})");
            ToolTipService.SetToolTip(more, moreLabel);
            AutomationProperties.SetName(more, moreLabel);
            ((TextBlock)columns[1]).Text = row.Download;
            ((TextBlock)columns[2]).Text = row.Upload;
            ToolTipService.SetToolTip(columns[1], row.HasGap ? Strings.Traffic_AppRunGap : null);
            ToolTipService.SetToolTip(columns[2], row.HasGap ? Strings.Traffic_AppRunGap : null);
        }
        displayedRows = rows;
        if (rows.Length == 0) pointerInRanking = false;
        ProcessTable.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    static void SetProcessAction(Button button, DisplayRow row, string format, string groupFormat)
    {
        button.Tag = row;
        var label = row.Group is { MemberCount: > 1 } ? string.Format(groupFormat, row.Process.Name) :
            string.Format(format, row.Process.Name, row.Process.Pid);
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
    }

    void OpenProcessAction(Button button, Action<TrafficProcessIdentity> action)
    {
        if (button.Tag is not DisplayRow row) return;
        if (row.Group is not { MemberCount: > 1 } group)
        {
            action(row.Process);
            return;
        }
        var menu = new MenuFlyout();
        foreach (var member in group.Members)
        {
            var item = new MenuFlyoutItem { Text = $"{member.Name} (PID {member.Pid})" };
            item.Click += (_, _) => action(member);
            menu.Items.Add(item);
        }
        if (group.MemberCount > group.Members.Length)
            menu.Items.Add(new MenuFlyoutItem { IsEnabled = false, Text = string.Format(Strings.Traffic_RankingMoreProcesses, group.MemberCount - group.Members.Length) });
        menu.Opening += (_, _) => processMenuOpen = true;
        menu.Closed += (_, _) => processMenuOpen = false;
        menu.ShowAt(button);
    }

    void OpenProcessMenu(Button button)
    {
        if (button.Tag is not DisplayRow row) return;
        var menu = new MenuFlyout();
        if (row.Group is { MemberCount: > 1 } group)
        {
            foreach (var member in group.Members)
            {
                var submenu = new MenuFlyoutSubItem { Text = $"{member.Name} (PID {member.Pid})" };
                AddProcessMenuItems(submenu.Items, member);
                menu.Items.Add(submenu);
            }
            if (group.MemberCount > group.Members.Length)
                menu.Items.Add(new MenuFlyoutItem { IsEnabled = false, Text = string.Format(Strings.Traffic_RankingMoreProcesses, group.MemberCount - group.Members.Length) });
        }
        else AddProcessMenuItems(menu.Items, row.Process);
        menu.Opening += (_, _) => processMenuOpen = true;
        menu.Closed += (_, _) => processMenuOpen = false;
        menu.ShowAt(button);
    }

    void AddProcessMenuItems(IList<MenuFlyoutItemBase> items, TrafficProcessIdentity process)
    {
        var connection = new MenuFlyoutItem { Text = Strings.Traffic_Connections };
        connection.Click += (_, _) => showConnections(process);
        items.Add(connection);
        AddFileItem(Strings.Common_OpenContainingFolder, false);
        AddFileItem(Strings.Launcher_Properties, true);
        var end = new MenuFlyoutItem { Text = Strings.Traffic_EndProcess, IsEnabled = !endingProcess && TrafficProcessFile.CanEnd(process) };
        end.Click += (_, _) =>
        {
            if (!shown || endingProcess) return;
            pendingEnd = process;
            EndProcessTarget.Text = string.Format(Strings.Traffic_EndProcessTarget, process.Pid, process.Name);
            ToolTipService.SetToolTip(EndProcessTarget, EndProcessTarget.Text);
            EndProcessConfirmation.Visibility = Visibility.Visible;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (shown && pendingEnd == process) CancelEndProcess.Focus(FocusState.Programmatic);
            });
        };
        items.Add(new MenuFlyoutSeparator());
        items.Add(end);

        void AddFileItem(string label, bool properties)
        {
            var item = new MenuFlyoutItem { Text = label, IsEnabled = TrafficProcessFile.HasTarget(process) };
            item.Click += async (_, _) => await RunFileActionAsync(process, properties, label);
            items.Add(item);
        }
    }

    void ClearEndConfirmation(bool restoreFocus)
    {
        pendingEnd = null;
        EndProcessConfirmation.Visibility = Visibility.Collapsed;
        if (restoreFocus) ProcessFilter.Focus(FocusState.Programmatic);
    }

    void CancelEndProcessClicked(object sender, RoutedEventArgs args) => ClearEndConfirmation(true);

    async void EndProcessClicked(object sender, RoutedEventArgs args)
    {
        if (!shown || endingProcess || pendingEnd is not { } process) return;
        ClearEndConfirmation(true);
        endingProcess = true;
        try
        {
            if (!await Task.Run(() => TrafficProcessFile.TryEnd(process)) && shown)
                ((App)Application.Current).ReportFailure(Strings.Traffic_EndProcess, new IOException(Strings.Traffic_EndProcessFailed));
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (shown) ((App)Application.Current).ReportFailure(Strings.Traffic_EndProcess, error);
        }
        finally { endingProcess = false; }
    }

    async Task RunFileActionAsync(TrafficProcessIdentity process, bool properties, string label)
    {
        var request = ++fileActionRequest;
        try
        {
            var path = await Task.Run(() => TrafficProcessFile.Resolve(process));
            if (!shown || request != fileActionRequest) return;
            if (path == null) throw new IOException(Strings.Traffic_ConnectionsProcessGone);
            if (properties) await new WindowsLauncherActionPlatform().PropertiesAsync(path, default);
            else await Task.Run(() => WindowsLauncherExecutor.CheckResult(LauncherNative.PaneLauncherReveal(path)));
        }
        catch (Exception error)
        {
            if (shown && request == fileActionRequest) ((App)Application.Current).ReportFailure(label, error);
        }
    }

    Grid CreateRow()
    {
        var grid = new Grid { Height = 30, ColumnSpacing = 10, BorderThickness = new(0, 0, 0, 1), BorderBrush = TogglePrototype.BorderBrush };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 3; index++)
        {
            var text = new TextBlock { FontSize = ProcessStatus.FontSize, VerticalAlignment = VerticalAlignment.Center };
            if (index == 0)
            {
                text.Foreground = ProcessTitle.Foreground;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
                text.Foreground = ProcessStatus.Foreground;
                text.TextAlignment = TextAlignment.Right;
            }
            Grid.SetColumn(text, index);
            grid.Children.Add(text);
        }
        AddAction("history", button => OpenProcessAction(button, showHistory), 3);
        AddAction("chev-d", OpenProcessMenu, 4);
        return grid;

        void AddAction(string glyph, Action<Button> action, int column)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            var button = new Button
            {
                Style = (Style)Resources["ProcessHistoryButton"], VerticalAlignment = VerticalAlignment.Center, IsTabStop = true,
                Content = new PaneIcon { Glyph = glyph, Width = 14, Height = 14 }
            };
            button.Click += (_, _) => action(button);
            Grid.SetColumn(button, column);
            grid.Children.Add(button);
        }
    }

    internal static string FormatRate(double? value) => value switch
    {
        >= 1048576 => $"{value / 1048576:N1} MiB/s",
        >= 1024 => $"{value / 1024:N1} KiB/s",
        null => "—",
        _ => $"{value:N0} B/s"
    };

    void CloseClicked(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
