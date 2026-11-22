using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Screenshots;
using Pane.Settings;
using Pane.Views;
using Windows.Graphics;

namespace Pane;

public sealed partial class App
{
    ScreenshotStore? screenshotStore;
    NativeScreenshotCapture? screenshotCapture;
    CancellationTokenSource? hotkeyCapture;
    NativeScreenshotOcr? screenshotOcr;
    OcrHistoryStore? ocrHistory;
    Window? screenshotHistoryWindow;
    RectInt32? screenshotHistoryBounds;
    readonly Dictionary<Guid, Window> desktopScreenshots = [];
    bool desktopScreenshotsEnabled;

    ScreenshotStore GetScreenshots() => screenshotStore ??= new(Path.Combine(Store.DirectoryPath, "screenshots"), Store.StorageSync);
    NativeScreenshotCapture GetScreenshotCapture() => screenshotCapture ??= new(GetScreenshots(),
        Path.Combine(Store.DirectoryPath, "capture-sessions"), TryPinScreenshot, RollBackScreenshotPin, Store.ScreenshotPinSync);
    NativeScreenshotOcr GetScreenshotOcr() => screenshotOcr ??= new(GetScreenshots(), WindowsScreenshotOcr.RecognizeAsync);

    void ShowScreenshots(Guid? id = null, bool temporaryTopmost = false)
    {
        if (closing || !AllowModule(ModuleIds.Screenshots)) return;
        HideCardDesktop();
        foreach (var panel in panels.Values) panel.Hide();
        if (screenshotHistoryWindow == null)
        {
            var view = new ScreenshotsView(GetScreenshots(), GetScreenshotCapture(), GetScreenshotOcr(),
                ocrHistory ??= new(Path.Combine(Store.DirectoryPath, OcrHistoryStore.DirectoryName), Store.StorageSync), IsScreenshotPinned,
                ToggleScreenshotPin, ScreenshotEdited, () => Store.Current.LimitScreenshotHistory,
                enabled => Store.Update(settings => settings with { LimitScreenshotHistory = enabled }), ShowTranslate, SaveToolResultAsNote);
            var window = new Window { Title = Strings.Module_Screenshots_Name, Content = view };
            var appWindow = window.AppWindow;
            var presenter = (OverlappedPresenter)appWindow.Presenter;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            AppIcon.Apply(hwnd);
            window.Activated += (_, args) =>
            {
                if (args.WindowActivationState != WindowActivationState.Deactivated || !presenter.IsAlwaysOnTop) return;
                queue.TryEnqueue(() =>
                {
                    if (screenshotHistoryWindow == window && Native.GetForegroundWindow() != hwnd && presenter.IsAlwaysOnTop)
                        presenter.IsAlwaysOnTop = false;
                });
            };
            if (screenshotHistoryBounds is { } bounds) appWindow.MoveAndResize(bounds);
            else
            {
                Native.GetCursorPos(out var cursor);
                var work = DisplayArea.GetFromPoint(new(cursor.X, cursor.Y), DisplayAreaFallback.Nearest).WorkArea;
                appWindow.Move(new(work.X, work.Y));
                var scale = Native.GetDpiForWindow(hwnd) / 96.0;
                var width = (int)Math.Ceiling(view.PreferredSize.Width * scale);
                var height = (int)Math.Ceiling(view.PreferredSize.Height * scale);
                appWindow.MoveAndResize(new(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 4, width, height));
            }
            void UpdateMinimum()
            {
                var scale = Native.GetDpiForWindow(hwnd) / 96.0;
                presenter.PreferredMinimumWidth = (int)Math.Ceiling(view.MinimumSize.Width * scale);
                presenter.PreferredMinimumHeight = (int)Math.Ceiling(view.MinimumSize.Height * scale);
            }
            UpdateMinimum();
            screenshotHistoryBounds = WindowBounds(appWindow);
            appWindow.Changed += (_, args) =>
            {
                if (args.DidPositionChange) UpdateMinimum();
                if (presenter.State == OverlappedPresenterState.Restored && (args.DidPositionChange || args.DidSizeChange))
                    screenshotHistoryBounds = WindowBounds(appWindow);
            };
            void ApplyTheme(object? sender, EventArgs args) => view.RequestedTheme = Glass.Dark ? ElementTheme.Dark : ElementTheme.Light;
            ApplyTheme(null, EventArgs.Empty);
            Glass.Changed += ApplyTheme;
            var close = new KeyboardAccelerator { Key = Windows.System.VirtualKey.W, Modifiers = Windows.System.VirtualKeyModifiers.Control };
            close.Invoked += (_, args) => { args.Handled = true; window.Close(); };
            view.KeyboardAccelerators.Add(close);
            window.Closed += (_, _) =>
            {
                screenshotHistoryWindow = null;
                Glass.Changed -= ApplyTheme;
                view.Dispose();
            };
            screenshotHistoryWindow = window;
        }
        var history = (ScreenshotsView)screenshotHistoryWindow.Content;
        if (id is { } imageId) history.Open(imageId);
        else history.EnsureHistoryLoaded();
        var historyPresenter = (OverlappedPresenter)screenshotHistoryWindow.AppWindow.Presenter;
        if (historyPresenter.State == OverlappedPresenterState.Minimized) historyPresenter.Restore();
        if (temporaryTopmost && !historyPresenter.IsAlwaysOnTop) historyPresenter.IsAlwaysOnTop = true;
        screenshotHistoryWindow.Activate();
    }

    async Task CaptureFromHotkeyAsync()
    {
        if (!desktopScreenshotsEnabled || !NativeScreenshotCapture.Available || hotkeyCapture != null || screenshotCapture?.IsRunning == true) return;
        if (screenshotHistoryWindow?.Content is ScreenshotsView openHistory)
        {
            await openHistory.CaptureNewAsync();
            return;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        hotkeyCapture = request;
        try
        {
            if (await GetScreenshotCapture().CaptureAsync(request.Token) is not { } result || request.IsCancellationRequested) return;
            if (result.Recognize) ShowScreenshots(result.Id);
            if (screenshotHistoryWindow?.Content is ScreenshotsView history)
                await history.ShowCapturedAsync(result.Id, result.Recognize);
            else if (Store.Current.LimitScreenshotHistory && !GetScreenshots().TrimUnpinnedHistory(result.Id, IsScreenshotPinned))
                shell?.NotifyOnce(Strings.Screenshots_ClearHistoryFailed);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) { ReportFailure(Strings.Screenshots_Capture, error); }
        finally { hotkeyCapture = null; }
    }

    bool IsScreenshotPinned(Guid id) => Store.Current.DesktopScreenshots?.ContainsKey(id) == true;

    DesktopScreenshotView CreateScreenshotCardView(Guid id, Action? removeFromWorkspace = null)
    {
        var store = GetScreenshots();
        var view = new DesktopScreenshotView(store.PathFor(id), store.IsEditable(id) && NativeScreenshotCapture.Available, removeFromWorkspace != null,
        Store.Current.DesktopScreenshotTopmost?.GetValueOrDefault(id) == true);
        view.OpenRequested += (_, _) => ShowScreenshots(id);
        view.EditRequested += async (_, _) => await EditPinnedScreenshotAsync(id, inWorkspace: removeFromWorkspace != null);
        view.UnpinRequested += (_, _) =>
        {
            if (removeFromWorkspace != null) removeFromWorkspace();
            else UnpinScreenshot(id);
        };
        view.TopmostChanged += (_, value) => queue.TryEnqueue(() =>
        {
            if (closing || !desktopScreenshots.TryGetValue(id, out var current)) return;
            Store.Update(settings => settings with
            { DesktopScreenshotTopmost = SetScreenshotTopmost(settings.DesktopScreenshotTopmost, id, value) });
            var presenter = (OverlappedPresenter)current.AppWindow.Presenter;
            if (presenter.IsAlwaysOnTop != value) presenter.IsAlwaysOnTop = value;
        });
        return view;
    }

    async Task EditPinnedScreenshotAsync(Guid id, bool inWorkspace = false)
    {
        if (closing || !desktopScreenshotsEnabled || !inWorkspace && !IsScreenshotPinned(id) || !GetScreenshots().IsEditable(id)) return;
        ShowScreenshots(id);
        if (screenshotHistoryWindow?.Content is ScreenshotsView history)
            await history.EditSelectedAsync(id);
    }

    void ScreenshotEdited(Guid id)
    {
        if (closing) return;
        var path = GetScreenshots().PathFor(id);
        if (desktopScreenshots.TryGetValue(id, out var card)) ((DesktopScreenshotView)card.Content).Apply(path);
        cardDesktopView?.UpdateScreenshot(id, path);
    }

    void ToggleScreenshotPin(Guid id)
    {
        var pinned = IsScreenshotPinned(id);
        if (pinned) { UnpinScreenshot(id); return; }
        ChangeScreenshotLayout(() => { if (TryPinScreenshot(id, null)) return; RollBackScreenshotPin(id); });
    }

    bool TryPinScreenshot(Guid id, (int X, int Y, int Width, int Height)? nativeBounds)
    {
        if (closing || !desktopScreenshotsEnabled || !GetScreenshots().Contains(id)) return false;
        if ((Store.Current.DesktopScreenshots?.Count ?? 0) >= ScreenshotStore.DesktopPinLimit)
        {
            shell?.NotifyOnce(Strings.Screenshots_PinLimit);
            return false;
        }
        SaveScreenshotLayout(id, true);
        RefreshDesktopScreenshots(newPin: id, nativeBounds: nativeBounds);
        return desktopScreenshots.ContainsKey(id);
    }

    bool RollBackScreenshotPin(Guid id)
    {
        if (IsScreenshotPinned(id)) UnpinScreenshot(id);
        return !IsScreenshotPinned(id);
    }

    void UnpinScreenshot(Guid id) => ChangeScreenshotLayout(() => SaveScreenshotLayout(id, false));

    void ChangeScreenshotLayout(Action change)
    {
        try { change(); RefreshDesktopScreenshots(); }
        catch (Exception error) when (ScreenshotStore.IsStorageError(error)) { ReportFailure(Strings.Screenshots_DesktopCard, error); }
    }

    static Dictionary<Guid, bool> SetScreenshotTopmost(Dictionary<Guid, bool>? source, Guid id, bool value)
    {
        var result = new Dictionary<Guid, bool>(source ?? []) { [id] = value };
        return result;
    }

    void SaveScreenshotLayout(Guid id, bool pinned, LauncherPosition? position = null) => Store.Update(settings =>
    {
        var layout = new Dictionary<Guid, LauncherPosition?>(settings.DesktopScreenshots ?? []);
        if (pinned) layout[id] = position;
        var sizes = settings.DesktopScreenshotSizes;
        if (!pinned)
        {
            layout.Remove(id);
            if (sizes?.ContainsKey(id) == true)
            {
                sizes = new(sizes);
                sizes.Remove(id);
            }
        }
        var topmost = settings.DesktopScreenshotTopmost;
        if (!pinned && topmost?.ContainsKey(id) == true) { topmost = new(topmost); topmost.Remove(id); }
        return settings with { DesktopScreenshots = layout.Count == 0 ? null : layout, DesktopScreenshotSizes = sizes is { Count: > 0 } ? sizes : null, DesktopScreenshotTopmost = topmost is { Count: > 0 } ? topmost : null };
    });

    void RefreshDesktopScreenshots(Guid? newPin = null, (int X, int Y, int Width, int Height)? nativeBounds = null)
    {
        if (closing || !desktopScreenshotsEnabled) return;
        var layout = Store.Current.DesktopScreenshots;
        foreach (var id in desktopScreenshots.Keys.ToArray())
            if (layout?.ContainsKey(id) != true && desktopScreenshots.Remove(id, out var removed)) removed.Close();
        if (layout != null)
            foreach (var (id, position) in layout.Take(8))
            {
                if (!GetScreenshots().Contains(id) || desktopScreenshots.ContainsKey(id)) continue;
                CreateDesktopScreenshot(id, position, temporaryTopmost: id == newPin && !desktopCardsHidden,
                    nativeBounds: id == newPin ? nativeBounds : null);
            }
        if (screenshotHistoryWindow?.Content is ScreenshotsView history) history.RefreshPin();
        RefreshDesktopCardsOverview();
    }

    void CreateDesktopScreenshot(Guid id, LauncherPosition? position, bool temporaryTopmost, (int X, int Y, int Width, int Height)? nativeBounds)
    {
        if (desktopScreenPoint == null || !HasDesktopCardPanels) SelectDesktopCardScreen();
        var view = CreateScreenshotCardView(id);
        var window = new Window { Title = Strings.Screenshots_DesktopCard, Content = view };
        var appWindow = window.AppWindow;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        AppIcon.Apply(hwnd);
        appWindow.IsShownInSwitchers = false;
        var presenter = (OverlappedPresenter)appWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = temporaryTopmost || Store.Current.DesktopScreenshotTopmost?.GetValueOrDefault(id) == true;
        window.Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated || !temporaryTopmost) return;
            temporaryTopmost = false;
            queue.TryEnqueue(() =>
            {
                if (desktopScreenshots.GetValueOrDefault(id) == window &&
                    Store.Current.DesktopScreenshotTopmost?.GetValueOrDefault(id) != true && presenter.IsAlwaysOnTop)
                    presenter.IsAlwaysOnTop = false;
            });
        };
        var caption = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(appWindow.Id);
        void UpdateCaption(object? sender, object? args)
        {
            if (view.XamlRoot is not { } root) return;
            var scale = root.RasterizationScale;
            caption.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption,
                [new(0, 0, (int)Math.Round(view.ActualWidth * scale), (int)Math.Round(view.ActualHeight * scale))]);
        }
        view.LayoutUpdated += UpdateCaption;
        void ApplyTheme(object? sender, EventArgs args) => view.RequestedTheme = Glass.Dark ? ElementTheme.Dark : ElementTheme.Light;
        ApplyTheme(null, EventArgs.Empty);
        Glass.Changed += ApplyTheme;
        Native.SubclassProc subclass = (h, message, w, l, subclassId, data) =>
        {
            if (message == 0xA5 && w == 2) // WM_NCRBUTTONUP in the image caption.
            {
                var point = new Native.Point { X = (short)(long)l, Y = (short)((long)l >> 16) };
                Native.ScreenToClient(h, ref point);
                var scale = Native.GetDpiForWindow(h) / 96.0;
                queue.TryEnqueue(() => view.ShowActions(new(point.X / scale, point.Y / scale)));
                return 0;
            }
            if (message == 0x232 && desktopArrangement == null) // WM_EXITSIZEMOVE
                queue.TryEnqueue(() =>
                {
                    if (desktopArrangement != null || desktopScreenshots.GetValueOrDefault(id) != window) return;
                    ChangeScreenshotLayout(() => Store.Update(settings => RecordDesktopScreenshotBounds(settings, id, appWindow)));
                });
            if (message == 0x7e || (message == 0x1a && w == 0x2f)) QueueDesktopReflow();
            return Native.DefSubclassProc(h, message, w, l);
        };
        window.Closed += (_, _) =>
        {
            view.LayoutUpdated -= UpdateCaption;
            Glass.Changed -= ApplyTheme;
            Native.RemoveWindowSubclass(hwnd, subclass, 1);
            view.Dispose();
        };
        if (!Native.SetWindowSubclass(hwnd, subclass, 1, 0))
        {
            window.Close();
            ReportFailure(Strings.Screenshots_DesktopCard, new System.ComponentModel.Win32Exception());
            return;
        }
        appWindow.Closing += (_, args) =>
        {
            if (desktopScreenshots.GetValueOrDefault(id) != window) return;
            args.Cancel = true;
            queue.TryEnqueue(() => UnpinScreenshot(id));
        };
        if (nativeBounds is { } selection)
        {
            // Enter the selection's monitor before measuring its resize border.
            appWindow.MoveAndResize(new(selection.X, selection.Y, selection.Width, selection.Height));
            var origin = new Native.Point { X = selection.X, Y = selection.Y };
            if (!Native.ScreenToClient(hwnd, ref origin)) throw new System.ComponentModel.Win32Exception();
            var frame = appWindow.Size;
            var client = appWindow.ClientSize;
            appWindow.MoveAndResize(new(appWindow.Position.X + origin.X, appWindow.Position.Y + origin.Y,
                selection.Width + frame.Width - client.Width, selection.Height + frame.Height - client.Height));
        }
        else PositionDesktopScreenshot(id, window, position);
        desktopScreenshots.Add(id, window);
        if (position == null) Store.Update(settings => RecordDesktopScreenshotBounds(settings, id, appWindow));
        if (desktopCardsHidden) return;
        ShowDesktopScreenshot(window);
        if (temporaryTopmost && !demo) GlassPanel.Activate(hwnd);
    }

    void PositionDesktopScreenshot(Guid id, Window window, LauncherPosition? position)
    {
        var point = desktopScreenPoint ?? default;
        var savedMonitor = position is { } savedPosition ? GlassPanel.MonitorById(savedPosition.MonitorId) : null;
        if ((savedMonitor ?? GlassPanel.MonitorAt(point.X, point.Y)) is not { } selectedMonitor)
            throw new System.ComponentModel.Win32Exception();
        var view = (DesktopScreenshotView)window.Content;
        var size = Store.Current.DesktopScreenshotSizes?.GetValueOrDefault(id) is [> 0, > 0] saved
            ? new Windows.Foundation.Size(saved[0], saved[1]) : view.PreferredSize;
        var area = System.Drawing.Rectangle.FromLTRB(selectedMonitor.Work.Left, selectedMonitor.Work.Top, selectedMonitor.Work.Right, selectedMonitor.Work.Bottom);
        var width = (int)Math.Round(size.Width * selectedMonitor.Dpi);
        var height = (int)Math.Round(size.Height * selectedMonitor.Dpi);
        var edge = (int)Math.Ceiling(8 * selectedMonitor.Dpi); // Existing desktop card work-area margin.
        System.Drawing.Point origin;
        if (position is { } anchor)
        {
            origin = new(area.Left + (int)Math.Round(anchor.X * selectedMonitor.Dpi), area.Top + (int)Math.Round(anchor.Y * selectedMonitor.Dpi));
            if (savedMonitor == null || !area.IntersectsWith(new(origin.X, origin.Y, width, height)))
                origin = new(
                    Math.Clamp(origin.X, area.Left + edge, Math.Max(area.Left + edge, area.Right - width - edge)),
                    Math.Clamp(origin.Y, area.Top + edge, Math.Max(area.Top + edge, area.Bottom - height - edge)));
        }
        else
            origin = DesktopCardPlacement.Find(area, new(width, height), edge, (int)Math.Ceiling(10 * selectedMonitor.Dpi),
                DesktopCardObstacles(exceptScreenshot: window)).Origin;
        var appWindow = window.AppWindow;
        var bounds = new RectInt32(origin.X, origin.Y, width, height);
        if (WindowBounds(appWindow).Equals(bounds)) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dpi = Native.GetDpiForWindow(hwnd);
        appWindow.MoveAndResize(bounds);
        // Restore the saved rectangle after WinUI handles the cross-monitor DPI change.
        if (Native.GetDpiForWindow(hwnd) != dpi) appWindow.MoveAndResize(bounds);
    }

    static void ShowDesktopScreenshot(Window window)
    {
        window.AppWindow.Show(activateWindow: false);
        if (window.AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: false })
            Native.SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(window), 1, 0, 0, 0, 0, 0x13);
    }

    static RectInt32 WindowBounds(AppWindow window) => new(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height);

    static PaneSettings RecordDesktopScreenshotBounds(PaneSettings settings, Guid id, AppWindow window)
    {
        var bounds = WindowBounds(window);
        if (GlassPanel.MonitorAt(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2) is not { } monitor) return settings;
        var positions = new Dictionary<Guid, LauncherPosition?>(settings.DesktopScreenshots!)
            { [id] = new(monitor.Id, (bounds.X - monitor.Work.Left) / monitor.Dpi, (bounds.Y - monitor.Work.Top) / monitor.Dpi) };
        var sizes = new Dictionary<Guid, double[]>(settings.DesktopScreenshotSizes ?? [])
            { [id] = [bounds.Width / (double)monitor.Dpi, bounds.Height / (double)monitor.Dpi] };
        return settings with { DesktopScreenshots = positions, DesktopScreenshotSizes = sizes };
    }

    void ClearDesktopScreenshots()
    {
        desktopScreenshotsEnabled = false;
        foreach (var id in desktopScreenshots.Keys.ToArray())
            if (desktopScreenshots.Remove(id, out var card)) card.Close();
        RefreshDesktopCardsOverview();
    }

    sealed class ScreenshotsModule(App app) : Capability(app, ModuleIds.Screenshots, ModuleCategory.System, "scissors")
    {
        public override Task StartAsync(CancellationToken token)
        {
            App.desktopScreenshotsEnabled = true;
            App.RefreshDesktopScreenshots();
            return Task.CompletedTask;
        }

        public override async Task StopAsync(CancellationToken token)
        {
            App.hotkeyCapture?.Cancel();
            App.screenshotHistoryWindow?.Close();
            App.ClearDesktopScreenshots();
            if (App.screenshotCapture is { } capture) await capture.DrainAsync();
            if (App.screenshotOcr is { } ocr) await ocr.DrainAsync();
        }
    }
}
