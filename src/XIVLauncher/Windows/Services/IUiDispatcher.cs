using System;
using System.Threading.Tasks;

namespace XIVLauncher.Windows.Services;

internal interface IUiDispatcher
{
    bool CheckAccess();

    void Invoke(Action action);

    Task InvokeAsync(Action action);
}
