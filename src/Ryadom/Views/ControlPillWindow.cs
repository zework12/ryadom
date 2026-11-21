using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Ryadom.Interop;
using Ryadom.ViewModels;

namespace Ryadom.Views;

/// <summary>Плашка сверху по центру монитора у того, кому помогают: кто управляет, сколько времени, «Отключить».
/// Не отнимает фокус, помощник её не видит. Можно сдвинуть влево-вправо, если мешает.</summary>
public class ControlPillWindow : Window
{
    private readonly HostViewModel _vm;
    private readonly ControlPill _pill;
    private Int32Rect _monitor;
    private Int32Rect _placed;
    private int _offsetX;          // сдвиг от центра, настоящие пиксели
    private bool _dragging, _placePending;
    private int _dragStartCursor, _dragStartOffset;

    public ControlPillWindow(HostViewModel vm, Int32Rect monitorPhysicalBounds)
    {
        _vm = vm;
        _monitor = monitorPhysicalBounds;
        DataContext = vm;
        Title = "Рядом — идёт помощь";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = _pill = new ControlPill();

        // Примерное место до показа; точное ставится в настоящих пикселях сразу после создания окна.
        var scale = Native.ScaleOf(monitorPhysicalBounds);
        var size = MeasurePill();
        Width = size.Width;
        Height = size.Height;
        Left = (monitorPhysicalBounds.X + (monitorPhysicalBounds.Width - size.Width * scale) / 2) / scale;
        Top = monitorPhysicalBounds.Y / scale;

        vm.PropertyChanged += OnVmChanged;
        Closed += (_, _) => vm.PropertyChanged -= OnVmChanged;
        Loaded += (_, _) => Place();
        DpiChanged += (_, _) => SchedulePlace();

        _pill.DragHandle.MouseLeftButtonDown += OnDragStart;
        _pill.DragHandle.MouseMove += OnDragMove;
        _pill.DragHandle.MouseLeftButtonUp += OnDragEnd;
        _pill.DragHandle.LostMouseCapture += (_, _) => _dragging = false;
    }

    public void MoveTo(Int32Rect monitorPhysicalBounds)
    {
        if (!_monitor.Equals(monitorPhysicalBounds)) _offsetX = 0;
        _monitor = monitorPhysicalBounds;
        Place();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelpers.MakeNoActivate(this);
        WindowHelpers.ExcludeFromCapture(this);
        Place();
    }

    /// <summary>Имя или время поменялись — ширина плашки могла измениться, ставим заново по центру.</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HostViewModel.ViewerName) or nameof(HostViewModel.DurationText) or null or "") SchedulePlace();
    }

    private void SchedulePlace()
    {
        if (_placePending) return;
        _placePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { _placePending = false; Place(); });
    }

    private Size MeasurePill()
    {
        _pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _pill.DesiredSize;
    }

    private void Place()
    {
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero) return; // ещё не показано
        if (_monitor.Width <= 0 || _monitor.Height <= 0) return;

        var scale = Native.ScaleOf(_monitor);
        var size = MeasurePill();
        var w = Math.Min(_monitor.Width, (int)Math.Ceiling(size.Width * scale));
        var h = Math.Min(_monitor.Height, (int)Math.Ceiling(size.Height * scale));
        var room = (_monitor.Width - w) / 2;
        _offsetX = Math.Clamp(_offsetX, -room, room);
        var rect = new Int32Rect(_monitor.X + room + _offsetX, _monitor.Y, w, h);
        if (rect.Equals(_placed) && IsVisible) { WindowHelpers.BringTopmost(this); return; }
        _placed = rect;
        WindowHelpers.PlaceExactly(this, rect);
    }

    // ── Перетаскивание по горизонтали ──

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source)) return;
        if (!Native.GetCursorPos(out var p)) return;
        _dragging = true;
        _dragStartCursor = p.X;
        _dragStartOffset = _offsetX;
        _pill.DragHandle.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || !Native.GetCursorPos(out var p)) return;
        _offsetX = _dragStartOffset + (p.X - _dragStartCursor);
        Place();
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _pill.DragHandle.ReleaseMouseCapture();
    }

    private static bool IsInsideButton(DependencyObject source)
    {
        for (var d = source; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Button) return true;
        return false;
    }
}
