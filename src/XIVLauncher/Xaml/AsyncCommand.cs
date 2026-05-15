using System.Windows.Input;
using Serilog;

namespace XIVLauncher.Xaml;

public class AsyncCommand
(
    Func<object?, Task> execute,
    Func<bool>?         canExecute = null
) : ICommand
{
    private bool          isExecuting;
    private EventHandler? canExecuteChanged;

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
                canExecuteChanged?.Invoke(this, EventArgs.Empty);
                await execute(parameter);
            }
            finally
            {
                isExecuting = false;
                canExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "执行 AsyncCommand 时发生错误");
        }
    }

    public void RaiseCanExecuteChanged() =>
        canExecuteChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler? CanExecuteChanged
    {
        add => canExecuteChanged += value;
        remove => canExecuteChanged -= value;
    }
}
