using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Ryadom.ViewModels;

namespace Ryadom.Theme;

/// <summary>Значок из линий на сетке 24×24. Цвет берёт у текста вокруг, толщина линии
/// задаётся в единицах экрана и не «толстеет» вместе с размером значка.</summary>
public sealed class IconView : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(IconView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(IconView), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(double), typeof(IconView), new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(IconView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double Stroke { get => (double)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 16 : Math.Min(16, availableSize.Width),
            double.IsInfinity(availableSize.Height) ? 16 : Math.Min(16, availableSize.Height));

    protected override void OnRender(DrawingContext dc)
    {
        var data = Data;
        if (data is null) return;
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;
        var scale = side / 24.0;
        var pen = new Pen(Foreground, Stroke / scale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        dc.PushTransform(new TranslateTransform((ActualWidth - side) / 2, (ActualHeight - side) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(Filled ? Foreground : null, pen, data);
        dc.Pop();
        dc.Pop();
    }
}

/// <summary>Полоски «качество связи»: 4 столбика, горит столько, сколько позволяет связь.</summary>
public sealed class LinkBars : FrameworkElement
{
    public static readonly DependencyProperty QualityProperty = DependencyProperty.Register(
        nameof(Quality), typeof(LinkQuality), typeof(LinkBars), new FrameworkPropertyMetadata(LinkQuality.Good, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GoodBrushProperty = Brush(nameof(GoodBrush));
    public static readonly DependencyProperty PoorBrushProperty = Brush(nameof(PoorBrush));
    public static readonly DependencyProperty LostBrushProperty = Brush(nameof(LostBrush));
    public static readonly DependencyProperty OffBrushProperty = Brush(nameof(OffBrush));

    private static DependencyProperty Brush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(LinkBars), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public LinkQuality Quality { get => (LinkQuality)GetValue(QualityProperty); set => SetValue(QualityProperty, value); }
    public Brush GoodBrush { get => (Brush)GetValue(GoodBrushProperty); set => SetValue(GoodBrushProperty, value); }
    public Brush PoorBrush { get => (Brush)GetValue(PoorBrushProperty); set => SetValue(PoorBrushProperty, value); }
    public Brush LostBrush { get => (Brush)GetValue(LostBrushProperty); set => SetValue(LostBrushProperty, value); }
    public Brush OffBrush { get => (Brush)GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(18, 14);

    protected override void OnRender(DrawingContext dc)
    {
        var (lit, brush) = Quality switch
        {
            LinkQuality.Excellent => (4, GoodBrush),
            LinkQuality.Good => (3, GoodBrush),
            LinkQuality.Poor => (2, PoorBrush),
            _ => (1, LostBrush),
        };
        for (var i = 0; i < 4; i++)
        {
            var h = 5 + i * 3;
            dc.DrawRoundedRectangle(i < lit ? brush : OffBrush, null, new Rect(i * 5, 14 - h, 3, h), 1, 1);
        }
    }
}

/// <summary>Мелкие присоединённые свойства для стилей.</summary>
public static class Ui
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(8)));
    public static CornerRadius GetRadius(DependencyObject o) => (CornerRadius)o.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject o, CornerRadius value) => o.SetValue(RadiusProperty, value);

    /// <summary>Серая подсказка в пустом поле ввода.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetPlaceholder(DependencyObject o) => (string)o.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject o, string value) => o.SetValue(PlaceholderProperty, value);

    /// <summary>Вторая строка в пункте выпадающего списка.</summary>
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.RegisterAttached(
        "Subtitle", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static string? GetSubtitle(DependencyObject o) => (string?)o.GetValue(SubtitleProperty);
    public static void SetSubtitle(DependencyObject o, string? value) => o.SetValue(SubtitleProperty, value);
}

/// <summary>Значение равно одному из вариантов в параметре («A|B») → видно. «!» в начале — наоборот.</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var p = parameter as string ?? "";
        var invert = p.StartsWith('!');
        if (invert) p = p[1..];
        var match = Array.IndexOf(p.Split('|'), value?.ToString() ?? "") >= 0;
        return match != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>true → видно; с параметром «!» — наоборот. Пустая строка и null считаются false.</summary>
public sealed class TruthyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int n => n > 0,
            _ => true,
        };
        if (parameter as string == "!") truthy = !truthy;
        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Список длиннее одного → видно (переключатель экранов).</summary>
public sealed class MoreThanOneToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is System.Collections.ICollection { Count: > 1 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>«123 456 789» → три группы цифр (каждая цифра потом рисуется в своей ячейке).</summary>
public sealed class CodeGroupsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Первая буква имени — для кружка-аватара.</summary>
public sealed class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string ?? "").Trim();
        return s.Length == 0 ? "?" : char.ToUpper(s[0], culture).ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
