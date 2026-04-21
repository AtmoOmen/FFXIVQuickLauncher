using System;
using System.Windows.Input;

namespace XIVLauncher.Xaml;

public class SyncCommand
(
    Action<object> command,
    Func<bool>     canExecute
) : ICommand
{
    public SyncCommand(Action<object> command)
        : this(command, () => true)
    {
    }

    public bool CanExecute(object? parameter) =>
        canExecute();

    public void Execute(object? parameter) =>
        command(parameter!);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
