using System.Windows;
using System.Windows.Controls;
using Ryadom.Viewer;

namespace Ryadom.Tests;

/// <summary>`Ryadom.exe --videotest out.txt`: несколько «сеансов» картинки подряд в одном окне
/// со сборкой мусора между ними и сменой размеров — так падала 1.5.4 при втором подключении.</summary>
public static class VideoTest
{
    public static async Task<int> RunAsync(string output)
    {
        var lines = new List<string>();
        var holder = new Border();
        var window = new Window { Width = 400, Height = 300, Content = holder, ShowInTaskbar = false, ShowActivated = false, Left = -2000, Top = -2000 };
        window.Show();
        var gone = new List<WeakReference>();
        var ok = true;
        for (var round = 1; round <= 4; round++)
        {
            var view = new VideoView();
            holder.Child = view;
            gone.Add(new WeakReference(view));
            await Task.Delay(300);
            // Сообщение окну этого сеанса должно попасть в этот же экземпляр, а не в прежний.
            var mine = false;
            view.FocusChanged += _ => mine = true;
            SendMessage(view.Hwnd, 0x0007 /* WM_SETFOCUS */, IntPtr.Zero, IntPtr.Zero);
            if (!mine) { lines.Add($"сеанс {round}: сообщения окна уходят в чужую картинку — ОШИБКА"); ok = false; }
            for (var i = 0; i < 5; i++)
            {
                window.Width = 400 + 37 * i;
                window.Height = 300 + 23 * i;
                window.UpdateLayout();
                await Task.Delay(50);
            }
            view.Dispose(); // как в конце сеанса: окно уничтожено, но элемент ещё в разметке
            await Task.Delay(100);
            holder.Child = null;
            view = null;
            // Пара проходов разметки, чтобы WPF отпустил старый элемент, и только потом сборка.
            for (var i = 0; i < 3; i++)
            {
                window.Width += 1;
                window.UpdateLayout();
                await Task.Delay(100);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            lines.Add($"сеанс {round}: живо; прежних картинок в памяти: {gone.Take(round).Count(w => w.IsAlive)}");
        }
        window.Close();
        lines.Add(ok ? "OK" : "ПРОВАЛ");
        await File.WriteAllLinesAsync(output, lines);
        return ok ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
