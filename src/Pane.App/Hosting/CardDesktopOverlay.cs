using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics;

namespace Pane.Hosting;

internal sealed class CardDesktopOverlay : IDisposable
{
    const string ClassName = "PaneCardDesktopOverlay";
    readonly DesktopWindowXamlSource island = new();
    readonly nint instance = Native.GetModuleHandleW(null);
    readonly Native.Proc procedure;
    readonly nint hwnd;

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
