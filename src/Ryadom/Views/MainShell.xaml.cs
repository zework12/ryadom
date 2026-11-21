using System.Windows;
using System.Windows.Controls;
using Ryadom.ViewModels;

namespace Ryadom.Views;

/// <summary>Содержимое главного окна. Окно говорит ему, какой сейчас экран и в каком оно состоянии.</summary>
public partial class MainShell : UserControl
{
    private ResourceDictionary? _dark;

    public MainShell() => InitializeComponent();

    /// <summary>Экран сменился: на сеансе — тёмная палитра, номер версии — только на первом экране.</summary>
    public void SetPage(object? page)
    {
        var dark = page is SessionViewModel;
        if (dark && _dark is null)
        {
            _dark = new ResourceDictionary { Source = new Uri("/Theme/Palette.Dark.xaml", UriKind.Relative) };
            Resources.MergedDictionaries.Add(_dark);
        }
        else if (!dark && _dark is not null)
        {
            Resources.MergedDictionaries.Remove(_dark);
            _dark = null;
        }
        StartFooter.Visibility = page is StartViewModel ? Visibility.Visible : Visibility.Collapsed;
        UninstallLink.Command = (page as StartViewModel)?.AskUninstallCommand;
    }

    /// <summary>Во весь экран: без полосы заголовка и рамки.</summary>
    public void SetFullscreen(bool on)
    {
        TitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        UpdateBorder(on, _maximized);
        _fullscreen = on;
    }

    /// <summary>Окно развёрнуто: края окна уходят за экран на <paramref name="overflow"/> — отступаем внутрь.</summary>
    public void SetMaximized(bool maximized, Thickness overflow)
    {
        _maximized = maximized;
        Margin = maximized ? overflow : new Thickness(0);
        MaxGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        MaxButton.ToolTip = maximized ? "Свернуть в окно" : "Развернуть";
        UpdateBorder(_fullscreen, maximized);
    }

    private bool _fullscreen, _maximized;

    private void UpdateBorder(bool fullscreen, bool maximized) =>
        Frame.BorderThickness = new Thickness(fullscreen || maximized ? 0 : 1);

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } w) SystemCommands.MinimizeWindow(w);
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } w) return;
        if (w.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(w);
        else SystemCommands.MaximizeWindow(w);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } w) SystemCommands.CloseWindow(w);
    }
}
