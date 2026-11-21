using System.Runtime.InteropServices;
using Ryadom.Shared;

namespace Ryadom.Viewer;

/// <summary>
/// Клавиатура помощника, пока фокус на картинке: все клавиши (в том числе
/// Win, Alt+Tab, Ctrl+Esc) уходят на тот компьютер и не трогают свой.
/// Перехват — низкоуровневый хук в отдельном потоке со своей очередью
/// сообщений: так он отвечает системе мгновенно, даже если окно занято.
/// Ctrl+Alt+Del перехватить нельзя — система его не отдаёт.
/// </summary>
public sealed class KeyboardGrab : IDisposable
{
    private readonly Action<byte[]> _send;
    private readonly HookProc _proc;
    private readonly Thread _thread;
    private uint _threadId;
    private volatile bool _active;
    private readonly HashSet<uint> _down = [];

    public KeyboardGrab(Action<byte[]> send)
    {
        _send = send;
        _proc = Hook;
        _thread = new Thread(Run) { IsBackground = true, Name = "Клавиатура" };
        _thread.Start();
    }

    /// <summary>Перехватывать сейчас (фокус на картинке и управление включено).</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (!value)
            {
                // Ушли из картинки с зажатой клавишей — на той стороне её отпустить.
                lock (_down)
                {
                    if (_down.Count > 0) _send(Protocol.EncodeReleaseAll());
                    _down.Clear();
                }
            }
        }
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) Log.Warn($"Перехват клавиатуры не поставился: {Marshal.GetLastWin32Error()}");
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
    }

    /// <summary>Главное окно «Рядом». Клавиши перехватываем, только пока оно впереди:
    /// фокус внутри свёрнутого или фонового окна не повод забирать чужие нажатия.</summary>
    public Func<IntPtr>? Root { get; set; }

    private IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _active && Root is { } root && GetForegroundWindow() == root())
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // Свои же нажатия, сделанные программно (не человеком), не трогаем.
            if ((info.Flags & LLKHF_INJECTED) == 0)
            {
                var up = (info.Flags & LLKHF_UP) != 0;
                var extended = (info.Flags & LLKHF_EXTENDED) != 0;
                var scan = (ushort)info.ScanCode;
                // Правый Shift и NumLock система помечает странно — чиним по коду клавиши.
                if (info.VkCode == 0xA1) { scan = 0x36; extended = false; }
                if (scan != 0)
                {
                    var key = (uint)scan | (extended ? 0x10000u : 0);
                    lock (_down)
                    {
                        if (up) _down.Remove(key);
                        else _down.Add(key);
                    }
                    _send(Protocol.EncodeKey(scan, up, extended));
                    // Caps/Num/Scroll Lock переключаем и у себя — лампочки
                    // на обоих компьютерах остаются одинаковыми.
                    if (info.VkCode is 0x14 or 0x90 or 0x91) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
                    return 1;
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        Active = false;
        if (_threadId != 0) PostThreadMessage(_threadId, 0x12 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }

    private const int WH_KEYBOARD_LL = 13;
    private const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint VkCode, ScanCode, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
