using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Pane.Hosting;

internal sealed class CardDesktopOverlay : IDisposable
{
    const string ClassName = "PaneCardDesktopOverlay";
    readonly DesktopWindowXamlSource island = new();
    readonly nint instance = Native.GetModuleHandleW(null);
    readonly Native.Proc procedure;
    readonly nint hwnd;
    internal nint Handle => hwnd;

    internal CardDesktopOverlay(UIElement content, Action close)
    {
        procedure = (window, message, w, l) =>
        {
            if (message == 0x10) { close(); return 0; }
            return Native.DefWindowProcW(window, message, w, l);
        };
        var wc = new Native.WndClass
        {
            size = (uint)Marshal.SizeOf<Native.WndClass>(), name = ClassName,
            instance = instance, proc = procedure, cursor = Native.LoadCursorW(0, 32512)
        };
        if (Native.RegisterClassExW(ref wc) == 0) throw new Win32Exception();
        // Use the same no-redirection XAML island path as GlassPanel. Creating the
        // HWND without a Win32 bitmap lets DWM blend the island with live desktop pixels.
        hwnd = Native.CreateWindowExW(0x00200088, ClassName, "", 0x80000000,
            0, 0, 0, 0, 0, 0, instance, 0);
        if (hwnd == 0) throw new Win32Exception();
        SetTransitionsDisabled(hwnd, true);
        island.Initialize(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        island.Content = content;
        island.SystemBackdrop = new TransparentBackdrop();
    }

    internal void Show(RectInt32 bounds, bool activate)
    {
        if (!Native.SetWindowPos(hwnd, -1, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10))
            throw new Win32Exception();
        island.SiteBridge.MoveAndResize(new(0, 0, bounds.Width, bounds.Height));
        island.SiteBridge.Show();
        Native.ShowWindow(hwnd, 4);
        if (activate)
        {
            GlassPanel.Activate(hwnd);
            island.NavigateFocus(new XamlSourceFocusNavigationRequest(XamlSourceFocusNavigationReason.Programmatic));
        }
    }

    internal void Hide() => Native.ShowWindow(hwnd, 0);

    internal static void SetTransitionsDisabled(nint window, bool disabled)
    {
        int value = disabled ? 1 : 0;
        Marshal.ThrowExceptionForHR(Native.DwmSetWindowAttribute(window, 3, ref value, sizeof(int))); // DWMWA_TRANSITIONS_FORCEDISABLED
    }

    internal static void ShowToolWindow(Window window)
    {
        var root = (FrameworkElement)window.Content;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        // A reused HWND can also expose its empty surface when shown again.
        int cloaked = 1;
        Marshal.ThrowExceptionForHR(Native.DwmSetWindowAttribute(hwnd, 13, ref cloaked, sizeof(int))); // DWMWA_CLOAK
        bool closed = false;
        CompositionTarget.Rendered += Reveal;
        window.Closed += Closed;
        window.AppWindow.Show(activateWindow: false);

        async void Reveal(object? sender, RenderedEventArgs args)
        {
            if (!root.IsLoaded) return;
            CompositionTarget.Rendered -= Reveal;
            await ElementCompositionPreview.GetElementVisual(root).Compositor.RequestCommitAsync();
            if (closed) return;
            window.Closed -= Closed;
            int visible = 0;
            Marshal.ThrowExceptionForHR(Native.DwmSetWindowAttribute(hwnd, 13, ref visible, sizeof(int)));
        }

        void Closed(object? sender, WindowEventArgs? args)
        {
            closed = true;
            CompositionTarget.Rendered -= Reveal;
            window.Closed -= Closed;
        }
    }

    public void Dispose()
    {
        Hide();
        island.Content = null;
        island.SystemBackdrop = null;
        island.Dispose();
        Native.DestroyWindow(hwnd);
        Native.UnregisterClassW(ClassName, instance);
    }
}
