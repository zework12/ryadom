using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Ryadom.Shared;
using Ryadom.ViewModels;

namespace Ryadom.Tests;

/// <summary>
/// Живая проверка окон без мыши и клавиатуры: две копии приложения жмут
/// свои кнопки сами (через команды моделей). Включается только переменной
/// RYADOM_AUTO = host:&lt;папка&gt; или viewer:&lt;папка&gt;. Пишет журнал шагов в папку;
/// у помощника снимает только полосу панели (без картинки чужого экрана).
/// </summary>
public static class AutoDrive
{
    public static bool NoInput => Environment.GetEnvironmentVariable("RYADOM_NO_INPUT") == "1";

    public static void Attach(MainViewModel main, StartViewModel start, Window window)
    {
        var spec = Environment.GetEnvironmentVariable("RYADOM_AUTO");
        if (string.IsNullOrEmpty(spec)) return;
        var role = spec.Split(':', 2)[0];
        var dir = spec.Split(':', 2)[1];
        Directory.CreateDirectory(dir);
        var logPath = Path.Combine(dir, role + ".log");
        void Say(string text) { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}"); Log.Info("auto: " + text); }
        Say("старт");
        if (role == "setup")
        {
            _ = SetupAsync(main, Environment.GetEnvironmentVariable("RYADOM_SETUP_ACTION") ?? "", Say);
            return;
        }
        _ = role == "host" ? HostAsync(main, start, Say) : ViewerAsync(main, start, window, dir, Say);
    }

    private static async Task<T> WaitPage<T>(MainViewModel main, Func<T, bool> ready, int seconds) where T : class
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            if (main.Page is T page && ready(page)) return page;
            await Task.Delay(100);
        }
        throw new TimeoutException($"не дождались {typeof(T).Name}");
    }

    /// <summary>Установщик: нажать нужную кнопку и записать, чем кончилось.</summary>
    private static async Task SetupAsync(MainViewModel main, string action, Action<string> say)
    {
        try
        {
            if (main.Page is not SetupViewModel vm) { say("не установщик: " + main.Page.GetType().Name); return; }
            say($"установщик: состояние {vm.State}, установлено {vm.IsInstalled} {vm.InstalledVersion}, этот {vm.ThisVersion}");
            switch (action)
            {
                case "install": vm.InstallCommand.Execute(null); break;
                case "reinstall": vm.ReinstallCommand.Execute(null); break;
                case "uninstall":
                    if (vm.State != SetupState.ConfirmUninstall) vm.UninstallCommand.Execute(null);
                    vm.ConfirmUninstallCommand.Execute(null);
                    break;
                default: say("только посмотреть"); return;
            }
            for (var i = 0; i < 100 && vm.State == SetupState.Working; i++) await Task.Delay(100);
            say($"после «{action}»: {vm.State} {vm.ErrorText}");
            if (vm.State == SetupState.Uninstalled) vm.CloseCommand.Execute(null);
        }
        catch (Exception error) { say("ОШИБКА " + error.Message); }
    }

    private static async Task HostAsync(MainViewModel main, StartViewModel start, Action<string> say)
    {
        try
        {
            start.NeedHelpCommand.Execute(null);
            var host = await WaitPage<HostViewModel>(main, h => h.State == HostState.Waiting, 40);
            var dir = Environment.GetEnvironmentVariable("RYADOM_AUTO")!.Split(':', 2)[1];
            File.WriteAllText(Path.Combine(dir, "code.txt"), host.Code.Replace(" ", ""));
            say("код выдан");
            await WaitPage<HostViewModel>(main, h => h.State == HostState.Asking, 60);
            say($"стучится: {host.ViewerName}");
            host.AllowCommand.Execute(null);
            await WaitPage<HostViewModel>(main, h => h.State == HostState.Connected, 10);
            say("разрешено, идёт помощь");
            await WaitPage<HostViewModel>(main, h => h.State == HostState.Waiting, 120);
            say($"помощник ушёл, длительность была {host.DurationText}");
            await Task.Delay(500);
            Application.Current.MainWindow.Close();
        }
        catch (Exception error) { say("ОШИБКА " + error.Message); Application.Current.MainWindow.Close(); }
    }

    private static async Task ViewerAsync(MainViewModel main, StartViewModel start, Window window, string dir, Action<string> say)
    {
        try
        {
            start.HelpOtherCommand.Execute(null);
            var join = await WaitPage<JoinViewModel>(main, _ => true, 5);
            var codeFile = Path.Combine(dir, "code.txt");
            for (var i = 0; i < 400 && !File.Exists(codeFile); i++) await Task.Delay(100);
            join.Name = "Проверка";
            join.Code = File.ReadAllText(codeFile);
            join.ConnectCommand.Execute(null);
            say("подключаюсь");
            var session = await WaitPage<SessionViewModel>(main, s => s.HasVideo, 60);
            say("картинка есть");
            if (Environment.GetEnvironmentVariable("RYADOM_AUTO_HOLD") == "1")
            {
                // Держать сеанс, пока не появится файл stop (для проверок через вход Claude).
                var stop = Path.Combine(dir, "stop");
                while (!File.Exists(stop)) await Task.Delay(500);
                File.Delete(stop);
            }
            await Task.Delay(4000);
            say($"окно {window.ActualWidth:F0}×{window.ActualHeight:F0} {window.WindowState}; {session.ActualFps} к/с, пинг {session.PingMs} мс, {session.ResolutionText}, экранов {session.Monitors.Count}");
            SaveBar(window, Path.Combine(dir, "viewer-bar.png"));
            session.SelectedFps = session.FpsOptions[2];
            say("90 кадров");
            await Task.Delay(6000);
            say($"после смены: {session.ActualFps} к/с, {session.ResolutionText}, HasVideo {session.HasVideo}");
            session.IsFullscreen = true;
            await Task.Delay(2500);
            say($"во весь экран: окно {window.ActualWidth:F0}×{window.ActualHeight:F0} {window.WindowStyle}; {session.ActualFps} к/с");
            session.IsFullscreen = false;
            await Task.Delay(1500);
            say($"обратно: окно {window.ActualWidth:F0}×{window.ActualHeight:F0} {window.WindowState}");
            session.DisconnectCommand.Execute(null);
            await WaitPage<JoinViewModel>(main, _ => true, 10);
            say("отключился");
            await Task.Delay(1000);
            window.Close();
        }
        catch (Exception error) { say("ОШИБКА " + error.Message); window.Close(); }
    }

    /// <summary>Снимок верхних 100 px окна (заголовок и панель) — без картинки экрана.</summary>
    private static void SaveBar(Window window, string path)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var r);
        var w = r.Right - r.Left;
        var h = Math.Min(100, r.Bottom - r.Top);
        using var bmp = new System.Drawing.Bitmap(w, r.Bottom - r.Top);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            var dc = g.GetHdc();
            PrintWindow(hwnd, dc, 2);
            g.ReleaseHdc(dc);
        }
        using var crop = bmp.Clone(new System.Drawing.Rectangle(0, 0, w, h), bmp.PixelFormat);
        crop.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
}
