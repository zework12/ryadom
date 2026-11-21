using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Ryadom.Host;
using Ryadom.ViewModels;
using Ryadom.Views;

namespace Ryadom.Tests;

/// <summary>`Ryadom.exe --pilltest out.txt`: плашка на секунду, затем проверка через
/// WindowFromPoint — пропускает ли она «мышь помощника» и ловит ли снова свою. Без ввода.</summary>
public static class PillTest
{
    public static async Task<int> RunAsync(string output)
    {
        var lines = new List<string>();
        var monitor = Monitors.All()[0];
        var vm = new HostViewModel { ViewerName = "Проверка", DurationText = "00:01", State = HostState.Connected };
        var pill = new ControlPillWindow(vm, monitor.Bounds);
        pill.Show();
        await Task.Delay(700);
        var hwnd = new WindowInteropHelper(pill).Handle;
        GetWindowRect(hwnd, out var r);
        var cx = (r.Left + r.Right) / 2;
        var cy = (r.Top + r.Bottom) / 2;
        bool PillAt() => GetAncestor(WindowFromPoint(new POINT { X = cx, Y = cy }), 2) == hwnd;

        var guard = new PillGuard();
        guard.Attach(hwnd);
        lines.Add($"плашка {r.Left},{r.Top}–{r.Right},{r.Bottom}; под центром плашка: {PillAt()}");
        guard.BeforeInjected(cx, cy);
        lines.Add($"мышь помощника над плашкой → под центром плашка: {PillAt()} (нужно False)");
        guard.BeforeInjected(r.Right + 50, r.Bottom + 50);
        lines.Add($"мышь помощника ушла → под центром плашка: {PillAt()} (нужно True)");
        guard.BeforeInjected(cx, cy);
        guard.Check(); // настоящий курсор не там, где помощник, — плашка снова своя
        lines.Add($"человек двигает свою мышь → под центром плашка: {PillAt()} (нужно True, если курсор не в центре плашки)");
        pill.Close();
        await File.WriteAllLinesAsync(output, lines);
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
