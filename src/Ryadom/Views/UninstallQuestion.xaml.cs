using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Ryadom.Views;

/// <summary>Вопрос об удалении программы. Команды кнопок задаёт экран, на котором он показан.</summary>
public partial class UninstallQuestion : UserControl
{
    public static readonly DependencyProperty ConfirmCommandProperty = DependencyProperty.Register(
        nameof(ConfirmCommand), typeof(ICommand), typeof(UninstallQuestion));
    public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.Register(
        nameof(CancelCommand), typeof(ICommand), typeof(UninstallQuestion));

    public ICommand? ConfirmCommand { get => (ICommand?)GetValue(ConfirmCommandProperty); set => SetValue(ConfirmCommandProperty, value); }
    public ICommand? CancelCommand { get => (ICommand?)GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }

    public UninstallQuestion()
    {
        InitializeComponent();
        // Вопрос появился — курсор клавиатуры на «Отмена»: случайный Enter ничего не удалит.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (IsVisible) CancelButton.Focus(); });
        };
    }
}
