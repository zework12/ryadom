using System.Windows;

namespace Ryadom;

public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        Net.Installer.FinishOnExit();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var preview = Array.IndexOf(e.Args, "--render-preview");
        if (preview >= 0 && preview + 1 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { Preview.PreviewRenderer.Render(e.Args[preview + 1]); }
            finally { Shutdown(); }
            return;
        }
        var shortcut = Array.IndexOf(e.Args, "--make-shortcut");
        if (shortcut >= 0 && shortcut + 1 < e.Args.Length)
        {
            // Проверка ярлыка в готовом файле: ярлык на себя в указанной папке.
            try { Net.Updater.Shortcut(e.Args[shortcut + 1], Environment.ProcessPath!); }
            catch (Exception error) { Shared.Log.Error("Ярлык", error); }
            Shutdown();
            return;
        }
        var pillTest = Array.IndexOf(e.Args, "--pilltest");
        if (pillTest >= 0 && pillTest + 1 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Tests.PillTest.RunAsync(e.Args[pillTest + 1]).ContinueWith(t => Dispatcher.BeginInvoke(() => Shutdown(t.IsFaulted ? 1 : 0)));
            return;
        }
        if (e.Args.Contains("--selftest"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Tests.SelfTest.RunAsync(e.Args).ContinueWith(t => Dispatcher.BeginInvoke(() => Shutdown(t.Result)));
            return;
        }
        AppController.Start(e.Args);
    }
}
