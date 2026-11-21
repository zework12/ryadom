using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows;
using Ryadom.Shared;

namespace Ryadom.Host;

/// <summary>
/// Мышь и клавиши помощника — в систему человека (SendInput). Клавиши
/// приходят скан-кодами (физическая клавиша), поэтому буквы печатаются в
/// раскладке человека — как если бы он нажал сам. Всё, что нажато,
/// запоминаем: при обрыве отпускаем, чтобы не залип Ctrl или кнопка мыши.
/// </summary>
public sealed class InputInjector
{
    private readonly Lock _gate = new();
    private readonly HashSet<(ushort Scan, bool Extended)> _keys = [];
    private readonly HashSet<byte> _buttons = [];
    private Int32Rect _monitor;

    public InputInjector(Int32Rect monitor) => _monitor = monitor;

    /// <summary>Плашка «Отключить»: под мышью помощника пропускает нажатия насквозь.</summary>
    public PillGuard? Pill { get; set; }

    private int _lastX, _lastY;

    /// <summary>Сменился показываемый монитор.</summary>
    public void SetMonitor(Int32Rect monitor)
    {
        lock (_gate) _monitor = monitor;
    }

    /// <summary>Разобрать пакет ввода из сети.</summary>
    public void Handle(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        lock (_gate)
        {
            switch (data[0])
            {
                case Protocol.Move when data.Length >= 5:
                    MoveTo(BinaryPrimitives.ReadUInt16LittleEndian(data[1..]) / 65535.0,
                        BinaryPrimitives.ReadUInt16LittleEndian(data[3..]) / 65535.0);
                    break;
                case Protocol.Button when data.Length >= 3:
                    Button(data[1], data[2] != 0);
                    break;
                case Protocol.Wheel when data.Length >= 4:
                    Wheel(BinaryPrimitives.ReadInt16LittleEndian(data[2..]), data[1] != 0);
                    break;
                case Protocol.Key when data.Length >= 4:
                    Key(BinaryPrimitives.ReadUInt16LittleEndian(data[1..]), (data[3] & Protocol.KeyUp) != 0, (data[3] & Protocol.KeyExtended) != 0);
                    break;
                case Protocol.ReleaseAll:
                    ReleaseAllLocked();
                    break;
            }
        }
    }

    public void ReleaseAll()
    {
        lock (_gate) ReleaseAllLocked();
    }

    /// <summary>Сочетание клавиш с панели помощника.</summary>
    public void Combo(string name)
    {
        (ushort Scan, bool Ext)[] keys = name switch
        {
            "taskmgr" => [(0x1D, false), (0x2A, false), (0x01, false)],
            "run" => [(0x5B, true), (0x13, false)],
            "win" => [(0x5B, true)],
            "alttab" => [(0x38, false), (0x0F, false)],
            "lang" => [(0x38, false), (0x2A, false)],
            _ => [],
        };
        if (keys.Length == 0) return;
        lock (_gate)
        {
            foreach (var k in keys) Key(k.Scan, up: false, k.Ext);
            Thread.Sleep(30);
            foreach (var k in keys.Reverse()) Key(k.Scan, up: true, k.Ext);
        }
    }

    /// <summary>Caps Lock / Num Lock — как у помощника (null — не трогать).</summary>
    public void SetLocks(bool? caps, bool? num)
    {
        lock (_gate)
        {
            if (caps is { } c && ((GetKeyState(0x14) & 1) != 0) != c) TapVk(0x14);
            if (num is { } n && ((GetKeyState(0x90) & 1) != 0) != n) TapVk(0x90);
        }
    }

    private static void TapVk(ushort vk)
    {
        var down = new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Flags = vk == 0x90 ? KEYEVENTF_EXTENDEDKEY : 0 } } };
        var up = new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Flags = KEYEVENTF_KEYUP | (vk == 0x90 ? KEYEVENTF_EXTENDEDKEY : 0) } } };
        SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
    }

    private void ReleaseAllLocked()
    {
        foreach (var key in _keys.ToList()) Key(key.Scan, up: true, key.Extended);
        foreach (var button in _buttons.ToList()) Button(button, down: false);
        _keys.Clear();
        _buttons.Clear();
    }

    private void MoveTo(double fx, double fy)
    {
        var desk = Monitors.VirtualDesktop();
        var x = _monitor.X + fx * Math.Max(0, _monitor.Width - 1);
        var y = _monitor.Y + fy * Math.Max(0, _monitor.Height - 1);
        _lastX = (int)Math.Round(x);
        _lastY = (int)Math.Round(y);
        Pill?.BeforeInjected(_lastX, _lastY);
        var nx = (int)Math.Round((x - desk.X) * 65535.0 / Math.Max(1, desk.Width - 1));
        var ny = (int)Math.Round((y - desk.Y) * 65535.0 / Math.Max(1, desk.Height - 1));
        SendMouse(nx, ny, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
    }

    private void Button(byte button, bool down)
    {
        uint flags;
        uint data = 0;
        switch (button)
        {
            case Protocol.Left: flags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
            case Protocol.Right: flags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
            case Protocol.Middle: flags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            case Protocol.X1: flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 1; break;
            case Protocol.X2: flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 2; break;
            default: return;
        }
        Pill?.BeforeInjected(_lastX, _lastY);
        if (down) _buttons.Add(button);
        else _buttons.Remove(button);
        SendMouse(0, 0, data, flags);
    }

    private static void Wheel(short delta, bool horizontal) =>
        SendMouse(0, 0, unchecked((uint)delta), horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL);

    private void Key(ushort scan, bool up, bool extended)
    {
        if (scan == 0) return;
        if (up) _keys.Remove((scan, extended));
        else _keys.Add((scan, extended));
        var flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
        var input = new INPUT
        {
            Type = INPUT_KEYBOARD,
            U = new InputUnion { Keyboard = new KEYBDINPUT { Scan = scan, Flags = flags } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendMouse(int x, int y, uint data, uint flags)
    {
        var input = new INPUT
        {
            Type = INPUT_MOUSE,
            U = new InputUnion { Mouse = new MOUSEINPUT { X = x, Y = y, MouseData = data, Flags = flags } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
        MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40,
        MOUSEEVENTF_XDOWN = 0x80, MOUSEEVENTF_XUP = 0x100, MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_HWHEEL = 0x1000,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public InputUnion U; }

    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
