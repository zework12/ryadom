using System.Runtime.InteropServices;

namespace Ryadom.Host;

/// <summary>
/// Плашка «Отключить» — только для человека. Помощник её не видит (она не
/// попадает в картинку), поэтому его мышь не должна в неё упираться: пока
/// его курсор над плашкой, плашка пропускает нажатия насквозь — в окно под
/// ней. Как только мышь двигает сам человек, плашка снова нажимается.
/// </summary>
public sealed class PillGuard
{
    private readonly Lock _gate = new();
    private IntPtr _hwnd;
    private bool _passThrough;
    private POINT _lastInjected;

    /// <summary>Окно плашки (или Zero — плашки нет).</summary>
    public void Attach(IntPtr hwnd)
    {
        lock (_gate)
        {
            _hwnd = hwnd;
            _passThrough = false;
        }
    }

    /// <summary>Помощник ставит курсор в точку (настоящие пиксели экрана) — до самого ввода.</summary>
    public void BeforeInjected(int x, int y)
    {
        lock (_gate)
        {
            if (_hwnd == IntPtr.Zero) return;
            _lastInjected = new POINT { X = x, Y = y };
            var inside = GetWindowRect(_hwnd, out var r) && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
            SetPassThrough(inside);
        }
    }

    /// <summary>Звать часто (раз в 100–200 мс): человек сдвинул мышь сам — плашка снова его.</summary>
    public void Check()
    {
        lock (_gate)
        {
            if (!_passThrough || _hwnd == IntPtr.Zero) return;
            if (GetCursorPos(out var p) && (Math.Abs(p.X - _lastInjected.X) > 2 || Math.Abs(p.Y - _lastInjected.Y) > 2))
                SetPassThrough(false);
        }
    }

    private void SetPassThrough(bool on)
    {
        if (_passThrough == on) return;
        _passThrough = on;
        var style = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        style = on ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED : style & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
