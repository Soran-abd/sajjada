using System.Runtime.InteropServices;
using System.Windows;

namespace Sajjada;

static class Native
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);

    public struct POINT { public int X, Y; }

    /// <summary>Cursor in physical screen pixels.</summary>
    public static Point CursorPos()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    // ---------- the window in physical screen pixels ----------
    // Positions in pixels mean the same on every screen, whatever its scaling, so the rug can be
    // carried from one screen to another and found again where it was left.

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);

    const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010, MONITOR_DEFAULTTONULL = 0;

    /// <summary>Top-left of a window in physical screen pixels.</summary>
    public static POINT WindowPos(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return new POINT { X = r.Left, Y = r.Top };
    }

    /// <summary>Width and height of a window in physical screen pixels.</summary>
    public static POINT WindowSize(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return new POINT { X = r.Right - r.Left, Y = r.Bottom - r.Top };
    }

    /// <summary>Moves a window, in physical screen pixels, without changing its size or layer.</summary>
    public static void MoveWindow(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>
    /// The usable part (without the taskbar) of the screen that holds most of the given area,
    /// in physical screen pixels.
    /// </summary>
    public static Rect WorkAreaOf(Rect area)
    {
        var r = new RECT { Left = (int)area.Left, Top = (int)area.Top, Right = (int)area.Right, Bottom = (int)area.Bottom };
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromRect(ref r, 2 /* MONITOR_DEFAULTTONEAREST */), ref info);
        var w = info.rcWork;
        return new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
    }

    /// <summary>Moves and resizes a window in one step, in physical screen pixels, without changing its layer.</summary>
    public static void SetWindowBounds(IntPtr hwnd, int x, int y, int width, int height) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>The whole screen nearest to a point, and its usable part without the taskbar, in physical pixels.</summary>
    public static (Rect Whole, Rect Work) ScreenAt(Point p)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromPoint(new POINT { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) }, 2 /* NEAREST */), ref info);
        Rect R(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return (R(info.rcMonitor), R(info.rcWork));
    }

    /// <summary>True when the point lies on a screen that is connected now.</summary>
    public static bool OnAScreen(int x, int y) =>
        MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONULL) != IntPtr.Zero;

    /// <summary>True when the point lies on the usable part (without the taskbar) of a connected screen.</summary>
    public static bool InWorkArea(Point p)
    {
        var monitor = MonitorFromPoint(new POINT { X = (int)Math.Floor(p.X), Y = (int)Math.Floor(p.Y) }, MONITOR_DEFAULTTONULL);
        if (monitor == IntPtr.Zero) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var w = info.rcWork;
        return p.X >= w.Left && p.X < w.Right && p.Y >= w.Top && p.Y < w.Bottom;
    }

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);

    /// <summary>True when the window is shown.</summary>
    public static bool IsVisible(IntPtr hwnd) => IsWindowVisible(hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    // Keeps a window directly above the desktop, under every other window.
    // HWND_BOTTOM would push it behind the desktop itself and hide it.
    const int WM_WINDOWPOSCHANGING = 0x0046;
    const uint SWP_NOZORDER = 0x0004, GW_HWNDPREV = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    public static IntPtr StayAtBottom(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0)
            {
                var aboveDesktop = GetWindow(GetShellWindow(), GW_HWNDPREV);
                if (aboveDesktop == hwnd) pos.flags |= SWP_NOZORDER;
                else pos.hwndInsertAfter = aboveDesktop;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }
}
