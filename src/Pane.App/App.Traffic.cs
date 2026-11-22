using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Traffic;
using Pane.Settings;
using Pane.Views;
using Windows.Graphics;

namespace Pane;

public sealed partial class App
{
    InterfaceTrafficMonitor? interfaceTraffic;
    ProcessTrafficMonitor? processTraffic;
    Task processTrafficStopped = Task.CompletedTask;
    GlassPanel? desktopTrafficCard;
    DispatcherQueueTimer? desktopTrafficWatch;
    DesktopTrafficView? desktopTrafficHud;
    bool desktopTrafficEnabled;
    Window? trafficWindow;
    TabView? trafficTabs;
    TrafficView? trafficLive;
    TrafficHistoryView? trafficHistory;
    RectInt32? trafficBounds;

    InterfaceTrafficMonitor GetInterfaceTraffic() => interfaceTraffic ??= new(queue, Store.Current.PauseInterfaceTraffic);

    void ShowTraffic()
    {
        HideCardDesktop();
        OpenTrafficWindow(false);
    }

    void ShowTrafficHistory(TrafficProcessIdentity? process = null) => OpenTrafficWindow(true, process);

    void OpenTrafficWindow(bool history, TrafficProcessIdentity? process = null, bool activate = true)
    {
        if (closing || !AllowModule(ModuleIds.Traffic)) return;
        foreach (var panel in panels.Values) panel.Hide();
        if (trafficWindow == null)
        {
            var live = new TrafficView(GetInterfaceTraffic(), processTraffic!, SetInterfaceTrafficPaused, ToggleTrafficPin, ShowTrafficHistory, ShowTrafficConnections);
            var ledger = new TrafficHistoryView();
            var tabs = new TabView { IsAddTabButtonVisible = false, CanDragTabs = false, CanReorderTabs = false, TabWidthMode = TabViewWidthMode.SizeToContent };
            tabs.TabItems.Add(new TabViewItem { Header = Strings.Traffic_Live, Content = live, IsClosable = false });
            tabs.TabItems.Add(new TabViewItem { Header = Strings.Traffic_History, Content = ledger, IsClosable = false });
            if (history) ledger.Open(process);
            tabs.SelectedIndex = history ? 1 : 0;
            live.Prepare();
            var window = new Window { Title = Strings.Module_Traffic_Name, Content = tabs };
            var appWindow = window.AppWindow;
            var presenter = (OverlappedPresenter)appWindow.Presenter;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            AppIcon.Apply(hwnd);
            Native.GetCursorPos(out var cursor);
            var display = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
            var work = display.WorkArea;
            if (trafficBounds is { } saved) appWindow.MoveAndResize(saved);
            else
            {
                appWindow.Move(new(work.X, work.Y));
                var dpi = Native.GetDpiForWindow(hwnd) / 96.0;
                int width = (int)Math.Round(TrafficHistoryView.PreferredSize.Width * dpi);
                int height = (int)Math.Round(TrafficHistoryView.PreferredSize.Height * dpi);
                appWindow.MoveAndResize(new(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 4, width, height));
            }
            void ApplyMinimum()
            {
                var dpi = Native.GetDpiForWindow(hwnd) / 96.0;
                presenter.PreferredMinimumWidth = (int)Math.Ceiling(TrafficHistoryView.MinimumSize.Width * dpi);
                presenter.PreferredMinimumHeight = (int)Math.Ceiling(TrafficHistoryView.MinimumSize.Height * dpi);
            }
            ApplyMinimum();
            window.SizeChanged += (_, _) => ApplyMinimum();
            appWindow.Changed += (_, args) =>
            {
                if (presenter.State == OverlappedPresenterState.Restored && (args.DidPositionChange || args.DidSizeChange))
                    trafficBounds = new(appWindow.Position.X, appWindow.Position.Y, appWindow.Size.Width, appWindow.Size.Height);
            };
            void ApplyTheme(object? sender, EventArgs args) => tabs.RequestedTheme = Glass.Dark ? ElementTheme.Dark : ElementTheme.Light;
            ApplyTheme(null, EventArgs.Empty);
            Glass.Changed += ApplyTheme;
            void ActivatePage()
            {
                if (tabs.SelectedIndex == 1) { live.OnHidden(); ledger.OnShown(); }
                else live.OnShown();
            }
            tabs.SelectionChanged += (_, _) => ActivatePage();
            var close = new KeyboardAccelerator { Key = Windows.System.VirtualKey.W, Modifiers = Windows.System.VirtualKeyModifiers.Control };
            close.Invoked += (_, args) =>
            {
                args.Handled = true;
                if (ReferenceEquals(cardDesktopTraffic?.Content, tabs)) SaveCardDesktopPlacement("traffic", null);
                else window.Close();
            };
            tabs.KeyboardAccelerators.Add(close);
            window.Closed += (_, _) =>
            {
                Glass.Changed -= ApplyTheme;
                live.Dispose();
                ledger.Dispose();
                trafficWindow = null;
                trafficTabs = null;
                trafficLive = null;
                trafficHistory = null;
            };
            trafficWindow = window;
            trafficTabs = tabs;
            trafficLive = live;
            trafficHistory = ledger;
            ActivatePage();
        }
        else
        {
            if (history) trafficHistory!.Open(process);
            trafficTabs!.SelectedIndex = history ? 1 : 0;
        }
        trafficLive!.RefreshPin(Store.Current.DesktopTraffic is { Hidden: false });
        if (!activate || ReferenceEquals(cardDesktopTraffic?.Content, trafficTabs)) return;
        if (trafficWindow.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } minimized)
            minimized.Restore();
        trafficWindow.Activate();
    }

    bool restoreOrdinaryTraffic;

    void OpenCardTrafficOutside()
    {
        if (trafficWindow is not { } window) return;
        HideCardDesktop();
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } minimized)
            minimized.Restore();
        window.Activate();
        if (trafficTabs?.SelectedIndex == 0) trafficLive?.OnShown();
    }

    void BorrowCardDesktopTraffic()
    {
        if (cardDesktopTraffic is not { Content: null } host) return;
        if (trafficWindow == null) OpenTrafficWindow(false, activate: false);
        if (trafficWindow is not { } window || trafficTabs is not { } tabs) return;
        restoreOrdinaryTraffic = window.AppWindow.IsVisible;
        if (restoreOrdinaryTraffic) window.AppWindow.Hide();
        window.Content = null;
        tabs.TabStripFooter = new Border
        {
            MinWidth = 32,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        host.Content = tabs;
        if (tabs.SelectedIndex == 0) trafficLive!.OnShown();
    }

    void ReturnCardDesktopTraffic()
    {
        if (cardDesktopTraffic is not { Content: TabView tabs } host || trafficWindow is not { } window) return;
        host.Content = null;
        tabs.TabStripFooter = null;
        window.Content = tabs;
        if (restoreOrdinaryTraffic) window.AppWindow.Show(activateWindow: false);
        else trafficLive?.OnHidden();
        restoreOrdinaryTraffic = false;
    }

    void SetInterfaceTrafficPaused(bool paused)
    {
        try
        {
            Store.Update(settings => settings.PauseInterfaceTraffic == paused ? settings : settings with { PauseInterfaceTraffic = paused });
            GetInterfaceTraffic().SetPaused(paused);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportFailure(Strings.Traffic_InterfaceMonitoring, error);
        }
    }

    void ShowTrafficConnections(TrafficProcessIdentity process) => Present(SecondaryId.TrafficConnections, Strings.Traffic_Connections,
        () => new TrafficConnectionsView(), view => ((TrafficConnectionsView)view).Open(process));

    void ToggleTrafficPin() => SetTrafficPin(Store.Current.DesktopTraffic is not { Hidden: false });

    DesktopTrafficView CreateTrafficCardView()
    {
        var view = new DesktopTrafficView(GetInterfaceTraffic(), processTraffic!);
        view.OpenRequested += (_, _) => ShowTraffic();
        view.UnpinRequested += (_, _) => SetTrafficPin(false);
        return view;
    }

    void SetTrafficPin(bool pinned)
    {
        try
        {
            Store.Update(settings => settings with { DesktopTraffic = (settings.DesktopTraffic ?? new()) with { Hidden = !pinned } });
            RefreshDesktopTraffic();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportFailure(Strings.Traffic_DesktopCard, error);
        }
    }

    void RefreshDesktopTraffic()
    {
        if (closing || !desktopTrafficEnabled) return;
        var layout = Store.Current.DesktopTraffic;
        if (layout is not { Hidden: false })
        {
            DisposeDesktopTraffic();
        }
        else if (desktopTrafficCard == null)
        {
            var view = CreateTrafficCardView();
            void Remember(Pane.Settings.LauncherPosition? anchor) =>
                Store.Update(settings => settings.DesktopTraffic is { } traffic
                    ? settings with { DesktopTraffic = traffic with { Position = anchor } } : settings);
            desktopTrafficCard = CreateDesktopCard(Strings.Traffic_DesktopCard, view, view, layout.Position, Remember, view.CardRadius);
            if (desktopTrafficCard is { } card)
            {
                desktopTrafficHud = view;
                view.ReadOptions = () => Store.Current.DesktopTraffic;
                view.SaveOptions = SaveTrafficHudOptions;
                view.RankingOffset = () =>
                {
                    var bounds = card.DesktopBounds;
                    var monitor = GlassPanel.MonitorAt(bounds.Left, bounds.Top);
                    return monitor is { } screen && bounds.Right + DesktopTrafficView.RankingWidth * screen.Dpi > screen.Work.Right
                        ? -DesktopTrafficView.RankingWidth : view.ActualWidth;
                };
                view.RevealEdge = () => ApplyTrafficEdgeTab(false);
                view.PointerLeft = () => ApplyTrafficEdgeTab(true);
                card.DesktopDragStarted = view.StartDrag;
                card.DesktopDragFinished = inside => { view.EndDrag(inside); ApplyTrafficEdgeTab(!inside); };
                card.DesktopClick = view.OpenDetails;
                card.DesktopDragSaved = anchor =>
                {
                    try { Remember(anchor); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { ReportFailure(Strings.Traffic_DesktopCard, error); }
                };
                card.Hiding += (_, _) => { view.OnHidden(); ApplyTrafficEdgeTab(false); };
                desktopTrafficWatch = queue.CreateTimer();
                // Match the existing desktop notes fullscreen check cadence.
                desktopTrafficWatch.Interval = TimeSpan.FromSeconds(2);
                desktopTrafficWatch.Tick += (_, _) => ApplyTrafficHudWindow();
                desktopTrafficWatch.Start();
                ApplyTrafficHudWindow();
            }
        }
        trafficLive?.RefreshPin(layout is { Hidden: false });
        RefreshDesktopCardsOverview();
    }

    void ClearDesktopTraffic()
    {
        desktopTrafficEnabled = false;
        DisposeDesktopTraffic();
        RefreshDesktopCardsOverview();
    }

    void DisposeDesktopTraffic()
    {
        desktopTrafficWatch?.Stop();
        desktopTrafficWatch = null;
        desktopTrafficCard?.Dispose();
        desktopTrafficCard = null;
        desktopTrafficHud = null;
    }

    void SaveTrafficHudOptions(bool topmost, bool edge, bool fullscreen)
    {
        try
        {
            Store.Update(settings => settings.DesktopTraffic is { } traffic ? settings with
            {
                DesktopTraffic = traffic with { Topmost = topmost, AutoHideAtEdge = edge, HideInFullscreen = fullscreen }
            } : settings);
            ApplyTrafficHudWindow();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ReportFailure(Strings.Traffic_DesktopCard, error); }
    }

    void ApplyTrafficEdgeTab(bool collapse, bool allowDock = true)
    {
        if (desktopTrafficCard is not { } card || desktopTrafficHud is not { } view) return;
        var dock = allowDock && Store.Current.DesktopTraffic?.AutoHideAtEdge == true;
        view.SetEdgeTab(card.SetDesktopEdgeTab(dock, collapse && !view.PointerInside));
    }

    void ApplyTrafficHudWindow()
    {
        if (desktopTrafficCard is not { } card || Store.Current.DesktopTraffic is not { } options) return;
        if (card.DesktopTopmost != options.Topmost)
        {
            card.DesktopTopmost = options.Topmost;
            if (card.Shown && !card.DesktopArranging) card.SetTopmost(options.Topmost);
        }
        bool fullscreen = false;
        if (card.Shown && options.HideInFullscreen)
        {
            var foreground = Native.GetForegroundWindow();
            if (foreground != 0 && !OwnWindow(foreground) && Native.GetWindowRect(foreground, out var rect))
            {
                var bounds = card.DesktopBounds;
                var monitor = GlassPanel.MonitorFromPoint(new GlassPanel.Point { X = bounds.Left, Y = bounds.Top }, 2);
                var info = new GlassPanel.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<GlassPanel.MonitorInfo>() };
                fullscreen = GlassPanel.GetMonitorInfoW(monitor, ref info) &&
                    rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top &&
                    rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
            }
        }
        if (card.DesktopOccluded != fullscreen)
        {
            if (fullscreen) desktopTrafficHud?.OnHidden();
            card.SetDesktopOccluded(fullscreen);
            if (!fullscreen && card.Shown) desktopTrafficHud?.OnShown();
        }
        ApplyTrafficEdgeTab(true);
    }

    Task StopProcessTrafficAsync()
    {
        if (processTraffic is { } monitor)
        {
            processTraffic = null;
            processTrafficStopped = monitor.StopAsync();
        }
        return processTrafficStopped;
    }

    sealed class TrafficModule(App app) : Capability(app, ModuleIds.Traffic, ModuleCategory.System, "globe")
    {
        public override Task StartAsync(CancellationToken token)
        {
            if (App.closing) return Task.CompletedTask;
            App.processTraffic = new(App.queue);
            App.desktopTrafficEnabled = true;
            App.RefreshDesktopTraffic();
            return Task.CompletedTask;
        }

        public override async Task StopAsync(CancellationToken token)
        {
            App.ReturnCardDesktopTraffic();
            App.trafficWindow?.Close();
            App.RemoveSecondary(SecondaryId.TrafficConnections);
            App.ClearDesktopTraffic();
            await App.StopProcessTrafficAsync();
        }
    }
}
