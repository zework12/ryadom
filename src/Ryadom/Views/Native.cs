using System.Runtime.InteropServices;
using System.Windows;

namespace Ryadom.Views;

/// <summary>Вызовы Windows, нужные только оформлению: где мышь, какой монитор, какой у него масштаб.</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Contains(POINT p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x20;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Весь монитор, на котором окно (вместе с панелью задач), и его рабочая область.</summary>
    public static bool TryGetMonitor(IntPtr hwnd, out RECT monitor, out RECT work)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var ok = GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info);
        monitor = info.rcMonitor;
        work = info.rcWork;
        return ok;
    }

    /// <summary>Масштаб монитора (1.0 = 100%), на который приходится прямоугольник в настоящих пикселях.</summary>
    public static double ScaleOf(Int32Rect physical)
    {
        try
        {
            var r = new RECT { Left = physical.X, Top = physical.Y, Right = physical.X + physical.Width, Bottom = physical.Y + physical.Height };
            if (GetDpiForMonitor(MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST), 0, out var dpi, out _) == 0 && dpi > 0) return dpi / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1.0;
    }

    /// <summary>Windows 11 скругляет углы окон — во весь экран это не нужно. На Windows 10 вызов просто ничего не делает.</summary>
    public static void SetRoundCorners(IntPtr hwnd, bool round)
    {
        try
        {
            var value = round ? 0 : 1; // DWMWCP_DEFAULT / DWMWCP_DONOTROUND
            _ = DwmSetWindowAttribute(hwnd, 33, ref value, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>Щелчки проходят сквозь окно (для всплывающего сообщения поверх картинки).</summary>
    public static void MakeTransparentForInput(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TRANSPARENT));
    }
}
