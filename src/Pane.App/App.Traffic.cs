using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
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
    bool trafficInWorkspace, restoreOrdinaryTraffic;
    RectInt32 trafficWorkspaceArea;
    double trafficWorkspaceScale;

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
        if (!cardDesktopShown || !cardDesktopCardsVisible) ReturnCardDesktopTraffic();
        foreach (var panel in panels.Values) panel.Hide();
        if (trafficWindow == null)
        {
            var live = new TrafficView(GetInterfaceTraffic(), processTraffic!, SetInterfaceTrafficPaused, ToggleTrafficPin, ShowTrafficHistory, ShowTrafficConnections);
            var ledger = new TrafficHistoryView();
            var tabs = new TabView { IsAddTabButtonVisible = false, CanDragTabs = false, CanReorderTabs = false,
                TabWidthMode = TabViewWidthMode.SizeToContent, VerticalAlignment = VerticalAlignment.Stretch };
            tabs.TabItems.Add(new TabViewItem { Header = Strings.Traffic_Live, Content = live, IsClosable = false });
            tabs.TabItems.Add(new TabViewItem { Header = Strings.Traffic_History, Content = ledger, IsClosable = false });
            if (history) ledger.Open(process);
            tabs.SelectedIndex = history ? 1 : 0;
            live.Prepare();
            var surface = new Border { Child = tabs, Style = (Style)Resources["PaneCardSurface"] };
            var window = new Window { Title = Strings.Module_Traffic_Name, Content = surface };
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
                if (!trafficInWorkspace && presenter.State == OverlappedPresenterState.Restored && (args.DidPositionChange || args.DidSizeChange))
                    trafficBounds = new(appWindow.Position.X, appWindow.Position.Y, appWindow.Size.Width, appWindow.Size.Height);
            };
            void ApplyTheme(object? sender, EventArgs args) => surface.RequestedTheme = Glass.Dark ? ElementTheme.Dark : ElementTheme.Light;
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
                if (trafficInWorkspace) SaveCardDesktopPlacement("traffic", null);
                else window.Close();
            };
            tabs.KeyboardAccelerators.Add(close);
            var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            escape.Invoked += (_, args) =>
            {
                if (!trafficInWorkspace) return;
                args.Handled = true;
                HideCardDesktop();
            };
            tabs.KeyboardAccelerators.Add(escape);
            appWindow.Closing += (_, args) =>
            {
                if (!trafficInWorkspace) return;
                args.Cancel = true;
                queue.TryEnqueue(() => SaveCardDesktopPlacement("traffic", null));
            };
            var caption = InputNonClientPointerSource.GetForWindowId(appWindow.Id);
            tabs.LayoutUpdated += (_, _) =>
            {
                if (tabs.TabStripFooter is not FrameworkElement { XamlRoot: { } root } footer) return;
                var rect = footer.TransformToVisual(null).TransformBounds(new(0, 0, footer.ActualWidth, footer.ActualHeight));
                var scale = root.RasterizationScale;
                caption.SetRegionRects(NonClientRegionKind.Caption, [new((int)Math.Round(rect.X * scale),
                    (int)Math.Round(rect.Y * scale), (int)Math.Round(rect.Width * scale), (int)Math.Round(rect.Height * scale))]);
            };
            Native.SubclassProc subclass = (h, message, w, l, id, data) =>
            {
                if (message == 0x232 && trafficInWorkspace) // WM_EXITSIZEMOVE
                    queue.TryEnqueue(() => SaveTrafficWorkspaceBounds(appWindow));
                return Native.DefSubclassProc(h, message, w, l);
            };
            if (!Native.SetWindowSubclass(hwnd, subclass, 1, 0)) throw new System.ComponentModel.Win32Exception();
            window.Closed += (_, _) =>
            {
                Native.RemoveWindowSubclass(hwnd, subclass, 1);
                Glass.Changed -= ApplyTheme;
                live.Dispose();
                ledger.Dispose();
                trafficWindow = null;
                trafficTabs = null;
                trafficLive = null;
                trafficHistory = null;
                trafficInWorkspace = restoreOrdinaryTraffic = false;
            };
            trafficWindow = window;
            trafficTabs = tabs;
            trafficLive = live;
            trafficHistory = ledger;
            if (activate) ActivatePage();
        }
        else
        {
            if (history) trafficHistory!.Open(process);
            trafficTabs!.SelectedIndex = history ? 1 : 0;
        }
        trafficLive!.RefreshPin(Store.Current.DesktopTraffic is { Hidden: false });
        if (!activate) return;
        if (trafficInWorkspace) { LocateCardTraffic(); return; }
        if (trafficWindow.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } minimized)
            minimized.Restore();
        trafficWindow.Activate();
        if (trafficTabs!.SelectedIndex == 0) trafficLive!.OnShown();
    }

    void LocateCardTraffic() => trafficWindow?.Activate();

    void OpenCardTrafficOutside()
    {
        HideCardDesktop();
        ReturnCardDesktopTraffic();
        if (trafficWindow is not { } window) { OpenTrafficWindow(false); return; }
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } minimized)
            minimized.Restore();
        window.Activate();
        if (trafficTabs?.SelectedIndex == 0) trafficLive?.OnShown();
    }

    void BorrowCardDesktopTraffic()
    {
        if (trafficInWorkspace && trafficWorkspaceArea == cardDesktopWorkArea)
        {
            var active = trafficWindow!.AppWindow;
            if (!active.IsVisible)
            {
                CardDesktopOverlay.ShowToolWindow(trafficWindow);
                SetCardToolZOrder(WinRT.Interop.WindowNative.GetWindowHandle(trafficWindow), true);
                if (trafficTabs!.SelectedIndex == 0) trafficLive!.OnShown();
            }
            return;
        }
        ReturnCardDesktopTraffic();
        if (trafficWindow == null) OpenTrafficWindow(false, activate: false);
        if (trafficWindow is not { } window || trafficTabs is not { } tabs) return;
        var placement = Store.Current.CardDesktopLayout!["traffic"];
        var appWindow = window.AppWindow;
        var presenter = (OverlappedPresenter)appWindow.Presenter;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        restoreOrdinaryTraffic = appWindow.IsVisible;
        trafficInWorkspace = true;
        appWindow.Hide();
        if (presenter.State != OverlappedPresenterState.Restored) presenter.Restore(false);
        PrepareCardToolWindow(appWindow, hwnd);
        tabs.TabStripFooter = new Border
        {
            MinWidth = 32,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        trafficWorkspaceArea = cardDesktopWorkArea;
        trafficWorkspaceScale = CardWorkspaceScale;
        PositionCardToolWindow(appWindow, trafficWorkspaceArea, trafficWorkspaceScale, placement, TrafficHistoryView.MinimumSize);
        CardDesktopOverlay.ShowToolWindow(window);
        SetCardToolZOrder(hwnd, true);
        if (tabs.SelectedIndex == 0) trafficLive!.OnShown();
    }

    void HideCardTraffic()
    {
        if (!trafficInWorkspace) return;
        trafficWindow!.AppWindow.Hide();
        trafficLive!.OnHidden();
    }

    void ReturnCardDesktopTraffic()
    {
        if (!trafficInWorkspace) return;
        var window = trafficWindow!;
        var tabs = trafficTabs!;
        var appWindow = window.AppWindow;
        var presenter = (OverlappedPresenter)appWindow.Presenter;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        appWindow.Hide();
        SetTrafficWorkspaceOwner(hwnd, 0);
        SetCardToolZOrder(hwnd, false);
        appWindow.IsShownInSwitchers = true;
        presenter.SetBorderAndTitleBar(true, true);
        presenter.IsMaximizable = presenter.IsMinimizable = true;
        SetTrafficWorkspaceBorder(hwnd, false);
        tabs.TabStripFooter = null;
        InputNonClientPointerSource.GetForWindowId(appWindow.Id).ClearRegionRects(NonClientRegionKind.Caption);
        if (trafficBounds is { } saved) appWindow.MoveAndResize(saved);
        trafficInWorkspace = false;
        if (restoreOrdinaryTraffic && !closing)
        {
            appWindow.Show(activateWindow: false);
            if (tabs.SelectedIndex == 0) trafficLive!.OnShown();
        }
        else trafficLive?.OnHidden();
        CardDesktopOverlay.SetTransitionsDisabled(hwnd, false);
        restoreOrdinaryTraffic = false;
    }

    double CardWorkspaceScale => Native.GetDpiForWindow(cardDesktopWindow!.Handle) / 96.0;

    OverlappedPresenter PrepareCardToolWindow(AppWindow window, nint hwnd)
    {
        SetTrafficWorkspaceOwner(hwnd, cardDesktopWindow!.Handle);
        window.IsShownInSwitchers = false;
        var presenter = (OverlappedPresenter)window.Presenter;
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = true;
        presenter.IsMaximizable = presenter.IsMinimizable = false;
        SetTrafficWorkspaceBorder(hwnd, true);
        CardDesktopOverlay.SetTransitionsDisabled(hwnd, true);
        return presenter;
    }

    static void PositionCardToolWindow(AppWindow window, RectInt32 work, double scale, CardDesktopPlacement placement, Windows.Foundation.Size minimum)
    {
        var width = (int)Math.Ceiling(Math.Max(placement.Width, minimum.Width) * scale);
        var height = (int)Math.Ceiling(Math.Max(placement.Height, minimum.Height) * scale);
        var x = work.X + (int)Math.Clamp(Math.Round(placement.X * scale), 0, Math.Max(0, work.Width - width));
        var y = work.Y + (int)Math.Clamp(Math.Round(placement.Y * scale), 0, Math.Max(0, work.Height - height));
        if (window.Position == new PointInt32(x, y) && window.Size == new SizeInt32(width, height)) return;
        window.Move(new(work.X, work.Y));
        window.MoveAndResize(new(x, y, width, height));
    }

    static void SetTrafficWorkspaceOwner(nint window, nint owner)
    {
        // GWLP_HWNDPARENT sets the owner of this top-level window, keeping it above the overlay.
        if (Native.SetWindowLongPtrW(window, -8, owner) == 0 && Marshal.GetLastWin32Error() != 0)
            throw new System.ComponentModel.Win32Exception();
    }

    static void SetTrafficWorkspaceBorder(nint window, bool workspace)
    {
        // Keep the native sizing frame and suppress only its visible DWM outline in the workspace.
        int color = workspace ? -2 : -1; // DWMWA_COLOR_NONE, DWMWA_COLOR_DEFAULT
        Marshal.ThrowExceptionForHR(Native.DwmSetWindowAttribute(window, 34, ref color, sizeof(int)));
    }

    static void SetCardToolZOrder(nint window, bool workspace)
    {
        // The overlay is topmost; showing a reused window must raise it without activating or reordering its owner.
        const uint flags = 0x0213; // SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE
        if (!Native.SetWindowPos(window, workspace ? -1 : -2, 0, 0, 0, 0, flags)) // HWND_TOPMOST / HWND_NOTOPMOST
            throw new System.ComponentModel.Win32Exception();
    }

    void SaveTrafficWorkspaceBounds(AppWindow window)
    {
        if (trafficInWorkspace) SaveCardToolBounds("traffic", window, trafficWorkspaceArea, trafficWorkspaceScale);
    }

    void SaveCardToolBounds(string key, AppWindow window, RectInt32 work, double scale)
    {
        if (Store.Current.CardDesktopLayout?.GetValueOrDefault(key) is not { } placement) return;
        var updated = placement with { X = (window.Position.X - work.X) / scale, Y = (window.Position.Y - work.Y) / scale,
            Width = window.Size.Width / scale, Height = window.Size.Height / scale };
        if (updated != placement) SaveCardDesktopPlacement(key, updated);
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
