using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Ryadom.Interop;

/// <summary>Приёмы для служебных окон поверх экрана у того, кому помогают.</summary>
public static class WindowHelpers
{
    /// <summary>Клики проходят сквозь окно (рамка вокруг экрана). Звать после SourceInitialized.</summary>
    public static void MakeClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Окно не попадает в картинку, которую видит помощник (Windows 10 2004+).
    /// Человек его видит, помощник — нет. Звать после SourceInitialized.</summary>
    public static void ExcludeFromCapture(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)) SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
    }

    /// <summary>Не отнимать фокус у других окон при показе/клике (плашка «Отключить»).</summary>
    public static void MakeNoActivate(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Поставить окно ровно на прямоугольник в настоящих пикселях экрана
    /// (WPF меряет в своих единицах, а на мониторе с масштабом 125% это не одно и то же).</summary>
    public static void PlaceExactly(Window window, Int32Rect physical)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        SetWindowPos(hwnd, HWND_TOPMOST, physical.X, physical.Y, physical.Width, physical.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Держать поверх всех окон (после того как другое topmost окно вылезло выше).</summary>
    public static void BringTopmost(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_MONITOR = 1, WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
