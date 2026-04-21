using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Serilog;

namespace XIVLauncher.Xaml;

public class AsyncCommand
(
    Func<object?, Task> execute,
    Func<bool>?         canExecute = null
) : ICommand
{
    private bool isExecuting;

    public bool CanExecute(object? parameter) =>
        !isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        try
        {
            if (!CanExecute(parameter))
                return;

            try
            {
                isExecuting = true;
                CommandManager.InvalidateRequerySuggested();
                await execute(parameter);
            }
            finally
            {
                isExecuting = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "执行 AsyncCommand 时发生错误");
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
