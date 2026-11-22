using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Localization;
using Pane.Services.Traffic;
using Pane.Views.Controls;
using Windows.Foundation;
using Windows.System;

namespace Pane.Views;

public sealed partial class TrafficHistoryView : UserControl, IDisposable
{
    readonly CancellationTokenSource lifetime = new();
    readonly PaneSegmented historyMode;
    TrafficProcessIdentity? process;
    TrafficHistoryQuery query;
    string? nextCursor;
    bool started, busy, disposed, canClear, reloadPending, includesPartialBuckets;
    int pageNumber;
    enum RangeOption { QuarterHour, Hour, Day, Week, Today, Yesterday, Month, Custom }
    RangeOption SelectedRange => (RangeOption)Range.SelectedIndex;
    bool ShowingApplications => process == null && historyMode.Value == "apps";

    internal TrafficHistoryView()
    {
        InitializeComponent();
        historyMode = new([("time", Strings.Traffic_HistoryByTime), ("apps", Strings.Traffic_HistoryByApplication)],
            "time", ModeStrong, ModeDim, ModeSurface.Background, ModeSurface.BorderBrush);
        AutomationProperties.SetName(historyMode, Strings.Traffic_HistoryGrouping);
        historyMode.Changed += (_, _) => { if (started) Reload(); };
        HistoryModeHost.Child = historyMode;
        ApplicationKeyword.MaxLength = TrafficHistoryQuery.ApplicationKeywordLimit;
        ApplicationSort.SelectedIndex = (int)TrafficApplicationSort.Total;
        Range.SelectedIndex = (int)RangeOption.Hour;
        StartDate.Date = EndDate.Date = DateTimeOffset.Now;
    }

    internal static Size PreferredSize => new(608, 464);
    internal static Size MinimumSize => new(500, 320);

    internal void Open(TrafficProcessIdentity? filter)
    {
        if (disposed || process?.InstanceId == filter?.InstanceId) return;
        process = filter;
        ProcessFilter.Visibility = filter == null ? Visibility.Collapsed : Visibility.Visible;
        ProcessFilterLabel.Text = filter == null ? "" : string.Format(Strings.Traffic_HistoryFilter, filter.Name, filter.Pid);
        ToolTipService.SetToolTip(ProcessFilterLabel, ProcessFilterLabel.Text);
        ClearButton.Visibility = filter == null ? Visibility.Visible : Visibility.Collapsed;
        HistoryModeHost.Visibility = filter == null ? Visibility.Visible : Visibility.Collapsed;
        ApplicationOptions.Visibility = ShowingApplications ? Visibility.Visible : Visibility.Collapsed;
        ConfirmClear.Visibility = Visibility.Collapsed;
        RecordedTotals.Visibility = Visibility.Collapsed;
        BucketNotice.Visibility = Visibility.Collapsed;
        canClear = false;
        nextCursor = null;
        SetBusy(busy);
        if (!started) return;
        if (busy)
        {
            reloadPending = true;
            SetRows([]);
            Status.Text = "";
            EmptyState.Text = Strings.Traffic_HistoryLoading;
        }
        else Reload();
    }

    public void OnShown()
    {
        if (started) return;
        started = true;
        Reload();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    void RangeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!started) return;
        CustomDates.Visibility = SelectedRange == RangeOption.Custom ? Visibility.Visible : Visibility.Collapsed;
        Reload();
    }

    void CustomDatesChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (!started || busy || disposed || SelectedRange != RangeOption.Custom) return;
        ResetResults(Strings.Traffic_HistoryDatesPending);
    }
    void ApplicationKeywordChanged(object sender, TextChangedEventArgs args) => ApplicationOptionsChanged();
    void ApplicationSortChanged(object sender, SelectionChangedEventArgs args) => ApplicationOptionsChanged();
    void ApplicationOptionsChanged()
    {
        if (started && !busy && !disposed && ShowingApplications) ResetResults(Strings.Traffic_HistoryOptionsPending);
    }

    void ApplicationKeywordKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        args.Handled = true;
        Reload();
    }

    void RefreshClicked(object sender, RoutedEventArgs args) => Reload();
    void NextClicked(object sender, RoutedEventArgs args) { if (nextCursor is { } cursor) LoadPage(cursor); }
    void ClearProcessFilterClicked(object sender, RoutedEventArgs args) => Open(null);

    void ArmClearClicked(object sender, RoutedEventArgs args)
    {
        if (busy || !canClear || process != null) return;
        ConfirmClear.Visibility = Visibility.Visible;
        CancelClear.Focus(FocusState.Programmatic);
    }

    void CancelClearClicked(object sender, RoutedEventArgs args)
    {
        ConfirmClear.Visibility = Visibility.Collapsed;
        ClearButton.Focus(FocusState.Programmatic);
    }

    async void ClearConfirmed(object sender, RoutedEventArgs args)
    {
        if (busy || !canClear || process != null || disposed || ConfirmClear.Visibility != Visibility.Visible) return;
        ConfirmClear.Visibility = Visibility.Collapsed;
        SetBusy(true);
        Status.Text = Strings.Traffic_HistoryClearing;
        var token = lifetime.Token;
        string notice;
        try
        {
            var cleared = await Task.Run(() => TrafficHistoryClient.ClearAsync(token), token);
            notice = cleared ? Strings.Traffic_HistoryCleared : Strings.Traffic_HistoryClearUnconfirmed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        if (disposed) return;
        reloadPending = false;
        SetBusy(false);
        Reload(notice);
    }

    void SetBusy(bool active)
    {
        busy = active;
        Range.IsEnabled = RefreshButton.IsEnabled = StartDate.IsEnabled = EndDate.IsEnabled = QueryDatesButton.IsEnabled = !active;
        historyMode.IsEnabled = !active && process == null;
        ApplicationKeyword.IsEnabled = ApplicationSort.IsEnabled = ApplyApplicationOptions.IsEnabled = !active;
        ClearButton.IsEnabled = !active && canClear && process == null;
        Next.IsEnabled = !active && nextCursor != null;
    }

    void Reload(string? notice = null)
    {
        if (busy || disposed) return;
        ApplicationOptions.Visibility = ShowingApplications ? Visibility.Visible : Visibility.Collapsed;
        var now = DateTimeOffset.UtcNow;
        includesPartialBuckets = false;
        if ((int)SelectedRange >= (int)RangeOption.Today)
        {
            var zone = TimeZoneInfo.Local;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            var (first, last) = SelectedRange switch
            {
                RangeOption.Custom => (StartDate.Date is { } startDate ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(startDate, zone).DateTime) : (DateOnly?)null,
                    EndDate.Date is { } endDate ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(endDate, zone).DateTime) : (DateOnly?)null),
                RangeOption.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
                RangeOption.Month => (new DateOnly(today.Year, today.Month, 1), today),
                _ => (today, today)
            };
            if (first is not { } from || last is not { } to || TrafficHistoryClient.CalendarQuery(from, to, now, zone,
                    process?.InstanceId, out includesPartialBuckets) is not { } calendar)
            {
                ResetResults(Strings.Traffic_HistoryInvalidDates);
                return;
            }
            query = calendar;
        }
        else
        {
            var (duration, resolution) = SelectedRange switch
            {
                RangeOption.QuarterHour => (TimeSpan.FromMinutes(15), 60),
                RangeOption.Day => (TimeSpan.FromDays(1), 3600),
                RangeOption.Week => (TimeSpan.FromDays(7), 3600),
                _ => (TimeSpan.FromHours(1), 60)
            };
            var start = (now - duration).ToUnixTimeSeconds();
            query = new(DateTimeOffset.FromUnixTimeSeconds(start - start % resolution), now, resolution, process?.InstanceId);
        }
        if (ShowingApplications) query = query with
        {
            ByApplication = true,
            ApplicationKeyword = ApplicationKeyword.Text.Trim(),
            ApplicationSort = (TrafficApplicationSort)ApplicationSort.SelectedIndex
        };
        pageNumber = 0;
        LoadPage(null, notice);
    }

    void ResetResults(string message)
    {
        nextCursor = null;
        ConfirmClear.Visibility = RecordedTotals.Visibility = BucketNotice.Visibility = Visibility.Collapsed;
        Status.Text = "";
        SetRows([]);
        EmptyState.Text = message;
        SetBusy(false);
    }

    async void LoadPage(string? cursor, string? notice = null)
    {
        if (busy || disposed) return;
        ConfirmClear.Visibility = Visibility.Collapsed;
        SetBusy(true);
        nextCursor = null;
        if (cursor == null)
        {
            SetRows([]);
            RecordedTotals.Visibility = Visibility.Collapsed;
            BucketNotice.Visibility = Visibility.Collapsed;
            EmptyState.Text = Strings.Traffic_HistoryLoading;
            Status.Text = notice ?? "";
        }
        else Status.Text = Strings.Traffic_HistoryLoading;
        var token = lifetime.Token;
        var interval = query;
        try
        {
            var page = await Task.Run(() => TrafficHistoryClient.ReadAsync(interval, cursor, token), token);
            if (disposed || reloadPending) return;
            canClear = page.State is TrafficHistoryState.Available or TrafficHistoryState.RangeTooLarge or TrafficHistoryState.CursorExpired;
            if (page.State != TrafficHistoryState.Available)
            {
                var message = page.State switch
                {
                    TrafficHistoryState.NotInstalled => Strings.Traffic_ProcessNotInstalled,
                    TrafficHistoryState.AccessDenied => Strings.Traffic_ProcessAccessDenied,
                    TrafficHistoryState.CursorExpired => Strings.Traffic_HistoryExpired,
                    TrafficHistoryState.RangeTooLarge => Strings.Traffic_HistoryNarrow,
                    TrafficHistoryState.ApplicationSummaryUnsupported => Strings.Traffic_HistorySummaryUnsupported,
                    TrafficHistoryState.ApplicationOptionsUnsupported => Strings.Traffic_HistoryOptionsUnsupported,
                    _ => Strings.Traffic_HistoryUnavailable
                };
                if (cursor == null) EmptyState.Text = message;
                else Status.Text = message;
                return;
            }

            nextCursor = page.Cursor;
            pageNumber++;
            SetRows(page.Rows, page.Applications);
            var count = page.Applications?.Count ?? page.Rows.Count;
            var filtered = !string.IsNullOrEmpty(interval.ApplicationKeyword);
            BucketNotice.Visibility = includesPartialBuckets && count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (page.Totals is { } totals && (count > 0 || page.Coverage != TrafficHistoryCoverage.Unavailable))
            {
                RecordedTotals.Text = string.Format(filtered ? Strings.Traffic_HistoryAllApplicationTotals : Strings.Traffic_HistoryRecordedTotals,
                    FormatBytes(totals.Download), FormatBytes(totals.Upload));
                RecordedTotals.Visibility = Visibility.Visible;
            }
            if (count == 0)
                EmptyState.Text = page.Coverage == TrafficHistoryCoverage.Unavailable ? Strings.Traffic_HistoryNoCoverage :
                    filtered ? Strings.Traffic_HistoryNoMatchingApplications : Strings.Traffic_HistoryEmpty;
            var coverage = page.Coverage switch
            {
                TrafficHistoryCoverage.Complete => string.Format(Strings.Traffic_HistoryThrough, page.CoveredThrough?.ToLocalTime()),
                TrafficHistoryCoverage.Gap => string.Format(Strings.Traffic_HistoryGaps, page.CoveredThrough?.ToLocalTime()),
                _ => count == 0 ? "" : Strings.Traffic_HistoryNoCoverage
            };
            Status.Text = count == 0 ? coverage : string.Format(Strings.Traffic_HistoryPage, pageNumber, coverage);
            if (notice != null) Status.Text = string.IsNullOrEmpty(Status.Text) ? notice : notice + " · " + Status.Text;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (!disposed)
            {
                SetBusy(false);
                if (reloadPending)
                {
                    reloadPending = false;
                    Reload();
                }
            }
        }
    }

    void SetRows(IReadOnlyList<TrafficHistoryRow> rows, IReadOnlyList<TrafficHistoryApplicationRow>? applications = null)
    {
        Entries.Items.Clear();
        var count = applications?.Count ?? rows.Count;
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Columns.Visibility = Entries.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TimeColumn.Width = new(applications == null ? 136 : 0);
        TimeLabel.Visibility = applications == null ? Visibility.Visible : Visibility.Collapsed;
        NameLabel.Text = applications == null ? Strings.Traffic_ProcessName : Strings.Traffic_HistoryApplication;
        if (applications != null)
            foreach (var application in applications) AddRow("", application.Name, application.Download, application.Upload, null, application.Path);
        else
            foreach (var row in rows) AddRow(row.Bucket.ToLocalTime().ToString("g"), row.Process.Name, row.Download, row.Upload, row.Process, null);
        if (Entries.Items.Count > 0) Entries.ScrollIntoView(Entries.Items[0]);

        void AddRow(string time, string name, decimal download, decimal upload, TrafficProcessIdentity? identity, string? path)
        {
            var cells = new[] { time, name, FormatBytes(download), FormatBytes(upload) };
            var grid = new Grid { ColumnSpacing = Columns.ColumnSpacing };
            foreach (var column in Columns.ColumnDefinitions) grid.ColumnDefinitions.Add(new() { Width = column.Width });
            for (var index = 0; index < cells.Length; index++)
            {
                var text = new TextBlock
                {
                    Text = cells[index], FontSize = TimeLabel.FontSize, VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = index > 1 ? TextAlignment.Right : TextAlignment.Left
                };
                if (index == 1 && path != null) ToolTipService.SetToolTip(text, path);
                if (index == 1 && process == null && identity != null)
                {
                    var label = string.Format(Strings.Traffic_HistoryOpenProcess, identity.Name, identity.Pid);
                    var cell = new Grid { ColumnSpacing = 4 };
                    cell.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                    cell.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                    var button = new Button
                    {
                        Style = (Style)Resources["ProcessHistoryAction"],
                        Content = new PaneIcon { Glyph = "history", Width = 14, Height = 14 }
                    };
                    AutomationProperties.SetName(button, label);
                    ToolTipService.SetToolTip(button, label);
                    button.Click += (_, _) => Open(identity);
                    Grid.SetColumn(button, 1);
                    cell.Children.Add(text);
                    cell.Children.Add(button);
                    Grid.SetColumn(cell, index);
                    grid.Children.Add(cell);
                    continue;
                }
                Grid.SetColumn(text, index);
                grid.Children.Add(text);
            }
            Entries.Items.Add(new ListViewItem { Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 30, Padding = new(8, 0, 8, 0) });
        }
    }

    internal static string FormatBytes(decimal value) => value switch
    {
        >= 1073741824 => $"{value / 1073741824m:N1} GiB",
        >= 1048576 => $"{value / 1048576m:N1} MiB",
        >= 1024 => $"{value / 1024m:N1} KiB",
        _ => $"{value:N0} B"
    };
}
