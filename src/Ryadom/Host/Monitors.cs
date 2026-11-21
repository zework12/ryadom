using System.Runtime.InteropServices;
using System.Windows;

namespace Ryadom.Host;

/// <summary>Монитор человека: ручка Windows и прямоугольник в настоящих пикселях.</summary>
public sealed record MonitorDesc(int Index, IntPtr Handle, Int32Rect Bounds, bool Primary, string Title);

public static class Monitors
{
    /// <summary>Все мониторы, основной первым.</summary>
    public static IReadOnlyList<MonitorDesc> All()
    {
        var raw = new List<(IntPtr Handle, Int32Rect Bounds, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(handle, ref info))
            {
                var r = info.Monitor;
                raw.Add((handle, new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), (info.Flags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        var ordered = raw.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.X).ToList();
        return ordered.Select((m, i) => new MonitorDesc(i, m.Handle, m.Bounds, m.Primary,
            ordered.Count == 1 ? "Экран" : m.Primary ? $"Экран {i + 1} (основной)" : $"Экран {i + 1}")).ToList();
    }

    /// <summary>Прямоугольник всего рабочего стола (все мониторы) — для абсолютных координат мыши.</summary>
    public static Int32Rect VirtualDesktop() =>
        new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
