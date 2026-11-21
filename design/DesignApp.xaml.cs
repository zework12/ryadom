using System.Windows;

namespace Ryadom;

/// <summary>Только превью: рисует экраны в PNG и выходит. Без аргумента — показывает главное окно с превью-моделью.</summary>
public partial class DesignApp : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var i = Array.IndexOf(e.Args, "--render-preview");
        var dir = i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : System.IO.Path.Combine(AppContext.BaseDirectory, "preview");
        try { Preview.PreviewRenderer.Render(dir); }
        finally { Shutdown(); }
    }
}
