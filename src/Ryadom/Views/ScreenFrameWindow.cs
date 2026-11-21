using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Ryadom.Interop;

namespace Ryadom.Views;

/// <summary>Тонкая цветная рамка по краю монитора у того, кому помогают: знак «сейчас управляют».
/// Щелчки проходят сквозь неё, помощник её не видит.</summary>
public class ScreenFrameWindow : Window
{
    /// <summary>Цвет знака «вами управляют» — общий для рамки и плашки.</summary>
    internal static readonly Color SignalColor = Color.FromRgb(0x19, 0xC3, 0x9A);

    private Int32Rect _bounds;

    public ScreenFrameWindow(Int32Rect physicalBounds)
    {
        _bounds = physicalBounds;
        Title = "Рядом — рамка";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Content = CreateFrame();

        // Примерное место до показа (точное — в настоящих пикселях — ставится сразу после создания окна).
        var scale = Native.ScaleOf(physicalBounds);
        Left = physicalBounds.X / scale;
        Top = physicalBounds.Y / scale;
        Width = Math.Max(1, physicalBounds.Width / scale);
        Height = Math.Max(1, physicalBounds.Height / scale);

        Loaded += (_, _) => Place();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
    }

    /// <summary>Сама рамка: яркая линия и тонкая тёмная кромка изнутри, чтобы читалась и на светлом, и на тёмном.</summary>
    internal static FrameworkElement CreateFrame() => new Border
    {
        BorderBrush = new SolidColorBrush(SignalColor),
        BorderThickness = new Thickness(4),
        IsHitTestVisible = false,
        Child = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0x04, 0x21, 0x1B)),
            BorderThickness = new Thickness(1),
        },
    };

    public void MoveTo(Int32Rect physicalBounds)
    {
        _bounds = physicalBounds;
        Place();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelpers.MakeClickThrough(this);
        WindowHelpers.ExcludeFromCapture(this);
        Place();
    }

    private void Place()
    {
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero) return; // ещё не показано
        if (_bounds.Width <= 0 || _bounds.Height <= 0) return;
        WindowHelpers.PlaceExactly(this, _bounds);
    }
}
