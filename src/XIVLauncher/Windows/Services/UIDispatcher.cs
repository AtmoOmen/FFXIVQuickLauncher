using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace XIVLauncher.Windows.Services;

internal sealed class UIDispatcher
(
    Dispatcher dispatcher
) : IUiDispatcher
{
    public bool CheckAccess() =>
        dispatcher.CheckAccess();

    public void Invoke(Action action)
    {
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    public Task InvokeAsync(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
