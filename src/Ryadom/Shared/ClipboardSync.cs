using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Ryadom.Shared;

/// <summary>
/// Общий буфер обмена на время помощи: скопировал текст у себя — он уже
/// у другого (и обратно). Только текст, до 256 КБ. Живёт в потоке окна.
/// </summary>
public sealed class ClipboardSync : IDisposable
{
    private const int MaxChars = 256 * 1024;
    private readonly Action<string> _send;
    private readonly HwndSource _source;
    private string? _lastRemote;

    public ClipboardSync(Action<string> send)
    {
        _send = send;
        _source = new HwndSource(new HwndSourceParameters("RyadomClipboard") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        _source.AddHook(Hook);
        AddClipboardFormatListener(_source.Handle);
    }

    /// <summary>Текст пришёл с той стороны — положить в свой буфер.</summary>
    public void SetFromRemote(string text)
    {
        if (text.Length > MaxChars) return;
        _lastRemote = text;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(30); // буфер держит другая программа
            }
        }
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x031D) // WM_CLIPBOARDUPDATE
        {
            try
            {
                if (!Clipboard.ContainsText()) return IntPtr.Zero;
                var text = Clipboard.GetText();
                if (text.Length == 0 || text.Length > MaxChars || text == _lastRemote) return IntPtr.Zero;
                _lastRemote = text;
                _send(text);
            }
            catch (COMException) { }
            catch (ExternalException) { }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(Hook);
        _source.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
