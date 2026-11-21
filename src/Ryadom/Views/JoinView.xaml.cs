using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Ryadom.Views;

public partial class JoinView : UserControl
{
    private bool _formatting;

    public JoinView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            // Сразу в поле кода; если код уже есть — в имя.
            if (!IsVisible) return;
            if (CodeBox.Text.Length == 0) CodeBox.Focus();
            else if (NameBox.Text.Length == 0) NameBox.Focus();
        });
    }

    /// <summary>В поле кода — только цифры (пробел и дефис не мешают, их уберёт раскладка по тройкам).</summary>
    private void OnCodePreviewInput(object sender, TextCompositionEventArgs e)
    {
        foreach (var c in e.Text)
        {
            if (char.IsDigit(c) || c is ' ' or '-') continue;
            e.Handled = true;
            return;
        }
    }

    /// <summary>Раскладывает введённое по три цифры: «123 456 789». Каретка остаётся у той же цифры.</summary>
    private void OnCodeChanged(object sender, TextChangedEventArgs e)
    {
        if (_formatting) return;
        var text = CodeBox.Text;
        var caret = Math.Min(CodeBox.CaretIndex, text.Length);
        var digitsBefore = 0;
        var digits = new StringBuilder(9);
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsDigit(text[i]) || digits.Length >= 9) continue;
            digits.Append(text[i]);
            if (i < caret) digitsBefore++;
        }

        var formatted = new StringBuilder(11);
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && i % 3 == 0) formatted.Append(' ');
            formatted.Append(digits[i]);
        }

        var result = formatted.ToString();
        if (result == text) return;
        _formatting = true;
        try
        {
            CodeBox.SetCurrentValue(TextBox.TextProperty, result);
            CodeBox.CaretIndex = Math.Min(result.Length, digitsBefore + (digitsBefore > 0 ? (digitsBefore - 1) / 3 : 0));
        }
        finally { _formatting = false; }
    }
}
