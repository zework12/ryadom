using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Ryadom.ViewModels;

namespace Ryadom.Views;

/// <summary>Экран сеанса: панель, полоса «связь прервалась», картинка. Здесь же логика выезжающей панели
/// полноэкранного режима и всплывающих сообщений.</summary>
public partial class SessionView : UserControl
{
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan IntroTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(4);

    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _noticeTimer;
    private SessionViewModel? _vm;
    private Window? _window;
    private DateTime _holdUntil;
    private DateTime? _leftAt;

    public SessionView()
    {
        InitializeComponent();

        BarPopup.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, 0), PopupPrimaryAxis.None)];
        NoticePopup.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, target.Height - popup.Height - 8 * VisualTreeHelper.GetDpi(this).DpiScaleY), PopupPrimaryAxis.None)];
        NoticePopup.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(NoticeHost) is HwndSource source) Native.MakeTransparentForInput(source.Handle);
        };

        // Мышь над картинкой WPF не видит (там чужое окно), поэтому положение курсора спрашиваем у системы.
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _poll.Tick += (_, _) => PollPointer();
        _noticeTimer = new DispatcherTimer { Interval = NoticeTime };
        _noticeTimer.Tick += (_, _) => HideNotice();

        DataContextChanged += (_, e) => Attach(e.NewValue as SessionViewModel);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => OnLayoutChanged();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.Deactivated += OnWindowDeactivated;
            _window.LocationChanged += OnWindowMoved;
            _window.StateChanged += OnWindowMoved;
        }
        Attach(DataContext as SessionViewModel);
        OnLayoutChanged();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.Deactivated -= OnWindowDeactivated;
            _window.LocationChanged -= OnWindowMoved;
            _window.StateChanged -= OnWindowMoved;
            _window = null;
        }
        Attach(null);
    }

    private void Attach(SessionViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        ApplyFullscreen();
        ApplyNotice();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionViewModel.IsFullscreen): ApplyFullscreen(); break;
            case nameof(SessionViewModel.Notice): ApplyNotice(); break;
        }
    }

    // ── Панель полноэкранного режима ──

    private void ApplyFullscreen()
    {
        Toolbar.CloseDropdowns();
        BarToolbar.CloseDropdowns();
        if (_vm is { IsFullscreen: true } && IsLoaded)
        {
            // Показываем панель на пару секунд, чтобы было видно, где она и как выйти.
            // Чуть позже — когда окно уже встало на весь экран, иначе панель окажется на старом месте.
            _holdUntil = DateTime.UtcNow + IntroTime;
            _leftAt = null;
            _poll.Start();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_vm is { IsFullscreen: true } && _window is { IsActive: true }) BarPopup.IsOpen = true;
            });
        }
        else
        {
            _poll.Stop();
            BarPopup.IsOpen = false;
        }
    }

    private void PollPointer()
    {
        if (_vm is not { IsFullscreen: true } || _window is null) { _poll.Stop(); BarPopup.IsOpen = false; return; }
        if (!_window.IsActive) { BarPopup.IsOpen = false; return; }

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetCursorPos(out var cursor) || !Native.GetWindowRect(hwnd, out var win)) return;
        var atTopEdge = cursor.X >= win.Left && cursor.X < win.Right && cursor.Y >= win.Top && cursor.Y <= win.Top + 2;
        var now = DateTime.UtcNow;

        if (!BarPopup.IsOpen)
        {
            if (atTopEdge) { _leftAt = null; BarPopup.IsOpen = true; }
            return;
        }

        if (now < _holdUntil || atTopEdge || BarToolbar.HasOpenDropdown || IsOverBar(cursor)) { _leftAt = null; return; }
        _leftAt ??= now;
        if (now - _leftAt >= HideDelay) BarPopup.IsOpen = false;
    }

    /// <summary>Курсор над панелью или в полоске между ней и верхним краем (с запасом вокруг).</summary>
    private bool IsOverBar(Native.POINT cursor)
    {
        if (PresentationSource.FromVisual(BarToolbar) is null) return false;
        var a = BarToolbar.PointToScreen(new Point(0, 0));
        var b = BarToolbar.PointToScreen(new Point(BarToolbar.ActualWidth, BarToolbar.ActualHeight));
        const int pad = 20;
        return cursor.X >= a.X - pad && cursor.X <= b.X + pad && cursor.Y <= b.Y + pad;
    }

    // ── Короткие сообщения ──

    private void ApplyNotice()
    {
        var text = _vm?.Notice;
        if (string.IsNullOrEmpty(text) || !IsLoaded || _window is not { IsActive: true }) { HideNotice(); return; }
        NoticeText.Text = text;
        _noticeTimer.Stop();
        _noticeTimer.Start();
        if (NoticePopup.IsOpen) Nudge(NoticePopup);
        else NoticePopup.IsOpen = true;
    }

    private void HideNotice()
    {
        _noticeTimer.Stop();
        NoticePopup.IsOpen = false;
    }

    // ── Окно ──

    /// <summary>Всплывающие окна WPF висят поверх ВСЕХ программ — уходя в другую программу, убираем их.</summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        BarPopup.IsOpen = false;
        HideNotice();
    }

    private void OnWindowMoved(object? sender, EventArgs e) => OnLayoutChanged();

    private void OnLayoutChanged()
    {
        Toolbar.SetAvailableWidth(ActualWidth);
        BarToolbar.SetAvailableWidth(ActualWidth - 64);
        BarHost.MaxWidth = Math.Max(320, ActualWidth);
        Nudge(BarPopup);
        Nudge(NoticePopup);
    }

    /// <summary>Всплывающее окно само за окном не ездит — заставляем пересчитать место.</summary>
    private static void Nudge(Popup popup)
    {
        if (!popup.IsOpen) return;
        popup.HorizontalOffset += 1;
        popup.HorizontalOffset -= 1;
    }

    // ── Для превью: то, что живёт во всплывающих окнах, рисуется отдельно ──
    internal FrameworkElement PreviewBar => BarHost;
    internal FrameworkElement PreviewNotice => NoticeHost;
    internal SessionToolbar PreviewToolbar => Toolbar;
    internal SessionToolbar PreviewBarToolbar => BarToolbar;
    internal FrameworkElement PreviewVideoArea => VideoArea;
    internal void PreviewSetNotice(string text) => NoticeText.Text = text;
    internal void PreviewLayout() => OnLayoutChanged();
}
