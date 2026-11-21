using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ryadom.Views;

public partial class ControlPill : UserControl
{
    public ControlPill()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Точка мягко «дышит» — видно, что помощь идёт прямо сейчас.
            var grow = new DoubleAnimation(1, 2.2, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever };
            var fade = new DoubleAnimation(0.45, 0, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever };
            Pulse.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            Pulse.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            Pulse.BeginAnimation(OpacityProperty, fade);
        };
    }

    /// <summary>Тело плашки: за него её двигают.</summary>
    internal FrameworkElement DragHandle => Body;
}
