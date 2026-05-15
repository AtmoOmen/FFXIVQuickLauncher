using System.Windows.Input;

namespace XIVLauncher.Xaml;

public class SyncCommand
(
    Action<object> command,
    Func<bool>     canExecute
) : ICommand
{
    private EventHandler? canExecuteChanged;

    public SyncCommand(Action<object> command)
        : this(command, () => true)
    {
    }

    public bool CanExecute(object? parameter) =>
        canExecute();

    public void Execute(object? parameter) =>
        command(parameter!);

    public void RaiseCanExecuteChanged() =>
        canExecuteChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler? CanExecuteChanged
    {
        add => canExecuteChanged += value;
        remove => canExecuteChanged -= value;
    }
}
