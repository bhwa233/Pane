using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Pane.Hosting;
using Pane.Localization;
using Pane.Settings;
using Windows.Foundation;
using Windows.Graphics;

namespace Pane.Views;

internal sealed class DesktopNoteWindow : IDisposable
{
    readonly Window window;
    readonly OverlappedPresenter presenter;
    readonly Native.SubclassProc subclass;
    readonly nint hwnd;
    readonly InputNonClientPointerSource caption;
    bool moving;
    internal DesktopNoteView View { get; }
    internal AppWindow AppWindow => window.AppWindow;
    internal bool Shown => AppWindow.IsVisible;
    internal bool TemporarilyTopmost { get; set; }
    internal RectInt32 Bounds => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
    internal (string Id, Native.Rect Work, float Dpi)? Monitor =>
        GlassPanel.MonitorAt(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);
    internal LauncherPosition? Position => Monitor is { } m
        ? new(m.Id, (Bounds.X - m.Work.Left) / m.Dpi, (Bounds.Y - m.Work.Top) / m.Dpi) : null;
    internal Size Size => new(Bounds.Width / Scale, Bounds.Height / Scale);
    double Scale => Native.GetDpiForWindow(hwnd) / 96.0;

    internal DesktopNoteWindow(DesktopNoteView view, GlassParameters glass, LauncherPosition? position,
        Native.Point screen, IReadOnlyList<System.Drawing.Rectangle> occupied, Action save, Action fold, Action reflow, Action refreshTopmost)
    {
        View = view;
        window = new Window { Title = Strings.Notes_DesktopCard, Content = view };
        window.Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated || !TemporarilyTopmost) return;
            TemporarilyTopmost = false;
            refreshTopmost();
        };
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var mode = RuntimeMode.Current.IsProduction ? "Release" : "Dev";
        Marshal.ThrowExceptionForHR(Native.PaneSetWindowTaskbarId(hwnd, $"Pane.{mode}.DesktopNote.{view.NoteId:N}"));
        AppIcon.Apply(hwnd);
        presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = true;
        presenter.IsMaximizable = presenter.IsMinimizable = false;
        window.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        caption = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        view.LayoutUpdated += UpdateCaption;
        void UpdateCaption(object? sender, object? args)
        {
            var title = (FrameworkElement)view.DragRegion;
            if (title.XamlRoot == null) return;
            var rect = title.TransformToVisual(view).TransformBounds(new(0, 0, title.ActualWidth, title.ActualHeight));
            double scale = title.XamlRoot.RasterizationScale;
            caption.SetRegionRects(NonClientRegionKind.Caption, [new((int)Math.Round(rect.X * scale),
                (int)Math.Round(rect.Y * scale), (int)Math.Round(rect.Width * scale), (int)Math.Round(rect.Height * scale))]);
        }
        void ApplyTheme(object? sender, EventArgs args) => view.RequestedTheme = glass.Dark ? ElementTheme.Dark : ElementTheme.Light;
        ApplyTheme(null, EventArgs.Empty);
        glass.Changed += ApplyTheme;
        var initialSize = view.AttachWindow(this);
        Reposition(position, screen, initialSize, occupied);
        UpdateSizeLimits();
        AppWindow.Changed += (_, e) => { if (e.DidPositionChange) UpdateSizeLimits(); };
        AppWindow.Closing += OnClosing;
        void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            args.Cancel = true;
            view.DispatcherQueue.TryEnqueue(fold.Invoke);
        }
        subclass = (h, message, w, l, id, data) =>
        {
            if (w == 2 && message == 0xA3) // HTCAPTION, WM_NCLBUTTONDBLCLK
            {
                view.DispatcherQueue.TryEnqueue(view.FocusBody);
                return 0;
            }
            if (w == 2 && message == 0xA5) // HTCAPTION, WM_NCRBUTTONUP
            {
                var point = new Native.Point { X = (short)(long)l, Y = (short)((long)l >> 16) };
                Native.ScreenToClient(h, ref point);
                var position = new Point(point.X / Scale, point.Y / Scale);
                view.DispatcherQueue.TryEnqueue(() => view.ShowNoteMenu(position));
                return 0;
            }
            if (message == 0x231) moving = true; // WM_ENTERSIZEMOVE
            if (message == 0x232 && moving) // WM_EXITSIZEMOVE
            {
                moving = false;
                view.DispatcherQueue.TryEnqueue(save.Invoke);
            }
            if (message == 0x7e || (message == 0x1a && w == 0x2f)) reflow();
            return Native.DefSubclassProc(h, message, w, l);
        };
        if (!Native.SetWindowSubclass(hwnd, subclass, 1, 0))
            throw new System.ComponentModel.Win32Exception();
        window.Closed += (_, _) =>
        {
            view.LayoutUpdated -= UpdateCaption;
            glass.Changed -= ApplyTheme;
            Native.RemoveWindowSubclass(hwnd, subclass, 1);
            view.Dispose();
        };
    }

    void UpdateSizeLimits()
    {
        var scale = Scale;
        presenter.PreferredMinimumWidth = (int)Math.Ceiling(DesktopNoteView.MinCardWidth * scale);
        presenter.PreferredMinimumHeight = (int)Math.Ceiling(DesktopNoteView.MinCardHeight * scale);
        presenter.PreferredMaximumWidth = (int)Math.Ceiling(720 * scale);
        presenter.PreferredMaximumHeight = (int)Math.Ceiling(900 * scale);
    }

    internal void Reposition(LauncherPosition? position, Native.Point screen, Size size, IReadOnlyList<System.Drawing.Rectangle> occupied)
    {
        if (((position is { } anchor ? GlassPanel.MonitorById(anchor.MonitorId) : null)
            ?? GlassPanel.MonitorAt(screen.X, screen.Y)) is not { } m)
            throw new System.ComponentModel.Win32Exception();
        var area = System.Drawing.Rectangle.FromLTRB(m.Work.Left, m.Work.Top, m.Work.Right, m.Work.Bottom);
        var width = (int)Math.Ceiling(size.Width * m.Dpi);
        var height = (int)Math.Ceiling(size.Height * m.Dpi);
        var edge = (int)Math.Ceiling(8 * m.Dpi); // Existing desktop card work-area margin.
        var point = position is { } saved && saved.MonitorId == m.Id
            ? new System.Drawing.Point(Math.Clamp(area.Left + (int)Math.Round(saved.X * m.Dpi), area.Left + edge, Math.Max(area.Left + edge, area.Right - width - edge)),
                Math.Clamp(area.Top + (int)Math.Round(saved.Y * m.Dpi), area.Top + edge, Math.Max(area.Top + edge, area.Bottom - height - edge)))
            : DesktopCardPlacement.Find(area, new(width, height), edge, (int)Math.Ceiling(10 * m.Dpi), occupied).Origin;
        AppWindow.MoveAndResize(new(point.X, point.Y, width, height));
    }

    internal void Resize(Size size) => AppWindow.Resize(new((int)Math.Ceiling(size.Width * Scale), (int)Math.Ceiling(size.Height * Scale)));
    internal void SetTopmost(bool value) => presenter.IsAlwaysOnTop = value;
    internal void Show() => AppWindow.Show(false);
    internal void Hide() => AppWindow.Hide();
    internal void Activate() => window.Activate();
    public void Dispose()
    {
        AppWindow.IsShownInSwitchers = false;
        Marshal.ThrowExceptionForHR(Native.PaneSetWindowTaskbarId(hwnd, null));
        window.Close();
    }
}
