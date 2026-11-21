using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Ryadom.Host;
using Ryadom.Shared;
using Ryadom.Viewer;
using Ryadom.ViewModels;
using Ryadom.Views;

namespace Ryadom;

/// <summary>Переходы между экранами и запуск сторон «Мне помогают» / «Я помогаю».</summary>
public static class AppController
{
    private static MainViewModel _main = null!;
    private static MainWindow _window = null!;
    private static readonly StartViewModel StartPage = new();
    private static HostController? _host;
    private static ViewerController? _viewer;
    private static JoinViewModel? _join;

    public static void Start(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Сбой", e.ExceptionObject as Exception);
        Application.Current.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Сбой в окне", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Сбой в фоне", e.Exception); e.SetObserved(); };

        var server = Array.IndexOf(args, "--server");
        if (server >= 0 && server + 1 < args.Length) Net.Api.Bases = [args[server + 1].TrimEnd('/')];
        Log.Info($"Запуск {typeof(AppController).Assembly.GetName().Version}, админ: {Elevation.IsElevated}");

        // Скачанный файл (не из папки программы) — это установщик.
        var self = Environment.ProcessPath ?? "";
        var setup = args.Contains("--uninstall") || (!Net.Installer.RunningInstalled && !Net.Updater.IsDevBuild(self));
        if (!setup)
        {
            Net.Updater.EnsureInstalled();
            _ = UpdateLoopAsync();
        }

        StartPage.NeedHelpCommand.Bind(_ => NeedHelp());
        StartPage.HelpOtherCommand.Bind(_ => HelpOther());
        StartPage.AskUninstallCommand.Bind(_ => StartPage.ConfirmingUninstall = true);
        StartPage.CancelUninstallCommand.Bind(_ => StartPage.ConfirmingUninstall = false);
        StartPage.ConfirmUninstallCommand.Bind(_ =>
        {
            Log.Info("Удаление из программы");
            Net.Installer.Uninstall();
            Application.Current.Shutdown();
        });
        object first = setup ? CreateSetup(args.Contains("--uninstall")) : StartPage;
        _main = new MainViewModel(first) { Version = typeof(AppController).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" };
        _window = new MainWindow(_main);
        Application.Current.MainWindow = _window;
        _window.Closing += (_, _) => Shutdown();
        _window.Show();
        Tests.AutoDrive.Attach(_main, StartPage, _window);

        if (args.Contains("--host")) OpenHost();
    }

    /// <summary>Экран установщика: установить / открыть / переустановить / удалить.</summary>
    private static SetupViewModel CreateSetup(bool uninstall)
    {
        var vm = new SetupViewModel { ThisVersion = Net.Updater.Current.ToString(3) };
        void Refresh()
        {
            var installed = Net.Updater.Newest();
            vm.IsInstalled = installed is not null;
            vm.InstalledVersion = installed?.Version.ToString(3);
        }
        Refresh();
        vm.State = uninstall && vm.IsInstalled ? SetupState.ConfirmUninstall : SetupState.Choose;

        async Task Run(string status, Action work, bool launch)
        {
            vm.StatusText = status;
            vm.State = SetupState.Working;
            try
            {
                await Task.Run(work);
                await Task.Delay(400); // чтобы «Устанавливаем…» не мелькнуло
                if (launch)
                {
                    Net.Installer.Launch();
                    Application.Current.Shutdown();
                    return;
                }
                vm.State = SetupState.Uninstalled;
            }
            catch (Exception error)
            {
                Log.Error(status, error);
                vm.ErrorText = "Не получилось: " + error.Message;
                vm.State = SetupState.Error;
            }
        }

        vm.InstallCommand.Bind(_ => _ = Run("Устанавливаем…", () => Net.Installer.Install(clean: false), launch: true));
        vm.ReinstallCommand.Bind(_ => _ = Run("Переустанавливаем…", () => Net.Installer.Install(clean: true), launch: true));
        vm.OpenCommand.Bind(_ =>
        {
            try
            {
                // Этот файл новее установленного — сначала обновить установку.
                if (Net.Updater.Newest() is { } have && have.Version < Net.Updater.Current) Net.Installer.Install(clean: false);
                Net.Installer.Launch();
                Application.Current.Shutdown();
            }
            catch (Exception error)
            {
                vm.ErrorText = "Не получилось открыть: " + error.Message;
                vm.State = SetupState.Error;
            }
        });
        vm.UninstallCommand.Bind(_ => vm.State = SetupState.ConfirmUninstall);
        vm.CancelCommand.Bind(_ => { Refresh(); vm.State = SetupState.Choose; });
        vm.ConfirmUninstallCommand.Bind(_ => _ = Run("Удаляем…", Net.Installer.Uninstall, launch: false));
        vm.CloseCommand.Bind(_ => Application.Current.Shutdown());
        return vm;
    }

    /// <summary>
    /// Проверять обновление при запуске и каждые полчаса. Если уже стоит версия
    /// новее запущенной и сейчас открыт первый экран (никто не помогает и не
    /// ждёт по коду) — тихо перезапуститься в новую. Во время помощи не трогаем.
    /// </summary>
    private static async Task UpdateLoopAsync()
    {
        while (true)
        {
            await Task.Run(() => Net.Updater.CheckAsync(CancellationToken.None));
            for (var i = 0; i < 360; i++) // 30 мин, проверяя раз в 5 с, не пора ли переключиться
            {
                if (TrySwitchToNewer()) return;
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static bool TrySwitchToNewer()
    {
        if (_main is null || !ReferenceEquals(_main.Page, StartPage) || StartPage.ConfirmingUninstall) return false;
        if (Net.Updater.Newest() is not { } newest || newest.Version <= Net.Updater.Current) return false;
        // Сверка по самому файлу: имя папки могло разойтись с версией внутри — тогда не перезапускаться по кругу.
        if (!Version.TryParse(System.Diagnostics.FileVersionInfo.GetVersionInfo(newest.Exe).FileVersion, out var real) || real <= Net.Updater.Current) return false;
        if (Tests.AutoDrive.Active) return false;
        try
        {
            Log.Info($"Переключаюсь на версию {newest.Version}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(newest.Exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(newest.Exe)!,
            });
            Application.Current.Shutdown();
            return true;
        }
        catch (Exception error)
        {
            Log.Warn($"Не переключился на новую версию: {error.Message}");
            return false;
        }
    }

    private static void NeedHelp()
    {
        if (!Elevation.IsElevated && !Environment.GetCommandLineArgs().Contains("--no-admin"))
        {
            // С правами администратора помощник сможет и окна «с щитом».
            // Отказался — работаем без них.
            var extra = string.Join(' ', Environment.GetCommandLineArgs().Skip(1).Where(a => a != "--host").Select(a => $"\"{a}\""));
            if (Elevation.RestartElevated(("--host " + extra).Trim()))
            {
                Application.Current.Shutdown();
                return;
            }
        }
        OpenHost();
    }

    private static void OpenHost()
    {
        var vm = new HostViewModel();
        _host = new HostController(vm, BringToFront, () => _ = BackAsync());
        _main.Page = vm;
        _ = _host.StartAsync();
    }

    private static void HelpOther()
    {
        _join ??= new JoinViewModel();
        _viewer ??= new ViewerController(_join, page => _main.Page = page, () => _ = BackAsync());
        _main.Page = _join;
    }

    private static async Task BackAsync()
    {
        var host = _host;
        _host = null;
        _main.Page = StartPage;
        if (host is not null) await host.DisposeAsync();
    }

    private static void BringToFront()
    {
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        var hwnd = new WindowInteropHelper(_window).Handle;
        SetForegroundWindow(hwnd);
        FlashWindow(hwnd, true);
    }

    private static void Shutdown()
    {
        // Сказать другой стороне «ухожу» и отпустить нажатое — но не дольше 3 с.
        var tasks = new List<Task>();
        if (_host is not null) tasks.Add(_host.DisposeAsync().AsTask());
        if (_viewer is not null) tasks.Add(_viewer.DisposeAsync().AsTask());
        _host = null;
        _viewer = null;
        if (tasks.Count > 0)
        {
            var all = Task.WhenAll(tasks);
            var frame = new System.Windows.Threading.DispatcherFrame();
            _ = Task.WhenAny(all, Task.Delay(3000)).ContinueWith(_ => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool FlashWindow(IntPtr hwnd, bool invert);
}
