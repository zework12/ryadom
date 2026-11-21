using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace Ryadom.ViewModels;

/// <summary>Основа моделей экранов: уведомление об изменениях всегда в потоке окна.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        else dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }
}

/// <summary>Команда-кнопка. Действие подставляет тот, кто ведёт экран (контроллер);
/// без действия кнопка неактивна — так модели можно собирать и для превью.</summary>
public sealed class RelayCommand : ICommand
{
    private Action<object?>? _execute;
    private Func<object?, bool>? _canExecute;

    public RelayCommand() { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public void Bind(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        Refresh();
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _execute is not null && (_canExecute?.Invoke(parameter) ?? true);

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute!(parameter);
    }

    public void Refresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        else dispatcher.BeginInvoke(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
    }
}
