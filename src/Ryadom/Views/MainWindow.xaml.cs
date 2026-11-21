using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Ryadom.ViewModels;

namespace Ryadom.Views;

/// <summary>Главное окно: своя полоса заголовка, текущий экран из <see cref="MainViewModel.Page"/>.
/// На экране сеанса разворачивается; <see cref="SessionViewModel.IsFullscreen"/> — без рамки на весь монитор.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private SessionViewModel? _session;
    private (WindowState State, Rect Bounds)? _beforeSession;
    private (WindowState State, Rect Bounds)? _beforeFullscreen;
    private bool _fullscreen;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        WindowChrome.SetWindowChrome(this, NormalChrome());
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon.ico", UriKind.Absolute)); }
        catch { /* значка нет в сборке — останется системный */ }

        vm.PropertyChanged += OnMainChanged;
        SourceInitialized += (_, _) => { ApplyPage(); UpdateFrame(); };
        StateChanged += (_, _) => UpdateFrame();
        SizeChanged += (_, _) => UpdateFrame();
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnMainChanged;
            if (_session is not null) _session.PropertyChanged -= OnSessionChanged;
        };
        Shell.SetPage(vm.Page);
    }

    private static WindowChrome NormalChrome() => new()
    {
        CaptionHeight = 40,
        ResizeBorderThickness = new Thickness(6),
        GlassFrameThickness = new Thickness(0, 0, 0, 1), // оставляет системную тень вокруг окна
        CornerRadius = new CornerRadius(0),
        UseAeroCaptionButtons = false,
    };

    private static WindowChrome FullscreenChrome() => new()
    {
        CaptionHeight = 0,
        ResizeBorderThickness = new Thickness(0),
        GlassFrameThickness = new Thickness(0),
        CornerRadius = new CornerRadius(0),
        UseAeroCaptionButtons = false,
    };

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Page) or null or "") ApplyPage();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsFullscreen)) ApplyFullscreen(_session?.IsFullscreen == true);
    }

    /// <summary>Экран сменился. Можно звать сколько угодно раз.</summary>
    private void ApplyPage()
    {
        var page = _vm.Page;
        var session = page as SessionViewModel;
        if (!ReferenceEquals(session, _session))
        {
            var wasSession = _session is not null;
            if (_session is not null) _session.PropertyChanged -= OnSessionChanged;
            _session = session;
            if (session is not null) session.PropertyChanged += OnSessionChanged;

            if (session is not null && !wasSession) EnterSession();
            else if (session is null && wasSession) LeaveSession();
        }
        Shell.SetPage(page);
        ApplyFullscreen(session?.IsFullscreen == true);
    }

    /// <summary>Начался сеанс: запоминаем, каким было окно, и разворачиваем.</summary>
    private void EnterSession()
    {
        _beforeSession = (WindowState == WindowState.Minimized ? WindowState.Normal : WindowState, CurrentBounds());
        WindowState = WindowState.Maximized;
    }

    /// <summary>Сеанс закончился: окно — как было до него.</summary>
    private void LeaveSession()
    {
        ApplyFullscreen(false);
        if (_beforeSession is not { } saved) return;
        _beforeSession = null;
        Restore(saved);
    }

    private void ApplyFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero) return; // окно ещё не создано — применится в SourceInitialized
        _fullscreen = on;

        if (on)
        {
            _beforeFullscreen = (WindowState == WindowState.Minimized ? WindowState.Maximized : WindowState, CurrentBounds());
            Native.TryGetMonitor(hwnd, out var monitor, out _);
            Shell.SetFullscreen(true);
            Shell.SetMaximized(false, default);
            WindowChrome.SetWindowChrome(this, FullscreenChrome());
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            Native.SetRoundCorners(hwnd, false);
            // Ровно на весь монитор в настоящих пикселях — тогда панель задач сама уходит под окно.
            Native.SetWindowPos(hwnd, IntPtr.Zero, monitor.Left, monitor.Top, monitor.Width, monitor.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        }
        else
        {
            Shell.SetFullscreen(false);
            Native.SetRoundCorners(hwnd, true);
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowChrome.SetWindowChrome(this, NormalChrome());
            if (_beforeFullscreen is { } saved) Restore(saved);
            _beforeFullscreen = null;
            UpdateFrame();
        }
    }

    private Rect CurrentBounds() => WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

    private void Restore((WindowState State, Rect Bounds) saved)
    {
        // Сначала обычное состояние с прежним местом и размером, потом — прежнее состояние:
        // так «развернуть → свернуть в окно» вернёт окно туда, где оно было.
        WindowState = WindowState.Normal;
        var b = saved.Bounds;
        if (!b.IsEmpty && b.Width > 0 && b.Height > 0 && !double.IsNaN(b.Left) && !double.IsNaN(b.Top))
        {
            Left = b.Left;
            Top = b.Top;
            Width = Math.Max(MinWidth, b.Width);
            Height = Math.Max(MinHeight, b.Height);
        }
        WindowState = saved.State;
    }

    /// <summary>У развёрнутого окна края выходят за экран на толщину системной рамки.
    /// Меряем, насколько именно, и отступаем содержимым внутрь.</summary>
    private void UpdateFrame()
    {
        if (_fullscreen) return;
        var maximized = WindowState == WindowState.Maximized;
        var overflow = new Thickness(0);
        var hwnd = Handle;
        if (maximized && hwnd != IntPtr.Zero
            && Native.TryGetMonitor(hwnd, out _, out var work)
            && Native.GetClientRect(hwnd, out var client))
        {
            var origin = new Native.POINT();
            Native.ClientToScreen(hwnd, ref origin);
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double Over(int px) => Math.Clamp(px, 0, 32) / scale;
            overflow = new Thickness(
                Over(work.Left - origin.X),
                Over(work.Top - origin.Y),
                Over(origin.X + client.Width - work.Right),
                Over(origin.Y + client.Height - work.Bottom));
        }
        Shell.SetMaximized(maximized, overflow);
    }
}
