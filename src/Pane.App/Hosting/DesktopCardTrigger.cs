using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace Pane.Hosting;

// Explorer owns the parent so ordinary applications and the taskbar keep their input.
internal sealed class DesktopCardTrigger : IDisposable
{
    const string ClassName = "PaneCardDesktopTrigger";
    readonly List<nint> windows = [];
    readonly Native.Proc procedure;
    readonly Action<Native.Point> open;
    readonly Func<bool> enabled;
    readonly nint instance = Native.GetModuleHandleW(null);
    readonly nint brush = CreateSolidBrush(0x00888A8D);

    internal DesktopCardTrigger(Action<Native.Point> open, Func<bool> enabled)
    {
        this.open = open;
        this.enabled = enabled;
        procedure = Handle;
        var wc = new Native.WndClass
        {
            size = (uint)Marshal.SizeOf<Native.WndClass>(), name = ClassName, instance = instance,
            proc = procedure, cursor = Native.LoadCursorW(0, 32649), background = brush
        };
        if (Native.RegisterClassExW(ref wc) == 0) throw new System.ComponentModel.Win32Exception();
    }

    internal void Refresh()
    {
        foreach (var window in windows) Native.DestroyWindow(window);
        windows.Clear();
        if (!enabled()) return;
        var shell = GetShellWindow();
        if (shell == 0) return;
        GetWindowThreadProcessId(shell, out var explorer);
        nint desktop = FindWindowExW(shell, 0, "SHELLDLL_DefView", null);
        if (desktop == 0)
            EnumWindows((window, _) =>
            {
                var name = new System.Text.StringBuilder(32);
                GetClassNameW(window, name, name.Capacity);
                GetWindowThreadProcessId(window, out var owner);
                if (owner == explorer && name.ToString() == "WorkerW")
                    desktop = FindWindowExW(window, 0, "SHELLDLL_DefView", null);
                return desktop == 0;
            }, 0);
        if (desktop == 0) return;
        foreach (var display in DisplayArea.FindAll())
        {
            var area = display.WorkArea;
            int center = area.X + area.Width / 2;
            if (GlassPanel.MonitorAt(center, area.Y + area.Height / 2) is not { } screen) continue;
            // Galaxy's desktop trigger geometry, measured in DIPs.
            int width = (int)Math.Round(120 * screen.Dpi), height = (int)Math.Round(8 * screen.Dpi);
            var origin = new Native.Point { X = center - width / 2, Y = area.Y + (int)Math.Round(2 * screen.Dpi) };
            if (!Native.ScreenToClient(desktop, ref origin)) continue;
            var hwnd = Native.CreateWindowExW(0x08000000, ClassName, "", 0x50000000,
                origin.X, origin.Y, width, height, desktop, 0, instance, 0);
            if (hwnd == 0) continue;
            Native.SetWindowPos(hwnd, 0, origin.X, origin.Y, width, height, 0x10);
            windows.Add(hwnd);
        }
    }

    nint Handle(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x21) return 3;
        if (message == 0x202 && enabled() && Native.GetCursorPos(out var point)) { open(point); return 0; }
        return Native.DefWindowProcW(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        foreach (var window in windows) Native.DestroyWindow(window);
        windows.Clear();
        Native.UnregisterClassW(ClassName, instance);
        Native.DeleteObject(brush);
    }

    delegate bool EnumWindow(nint window, nint state);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, nint state);
    [DllImport("user32.dll")] static extern nint GetShellWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern nint FindWindowExW(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int GetClassNameW(nint window, System.Text.StringBuilder name, int capacity);
    [DllImport("gdi32.dll")] static extern nint CreateSolidBrush(uint color);
}
