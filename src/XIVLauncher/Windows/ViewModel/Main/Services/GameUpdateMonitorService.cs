using Serilog;
using XIVLauncher.Common.Game;
using XIVLauncher.GamePatchV3.Update;
using XIVLauncher.Login.Models;

namespace XIVLauncher.Windows.ViewModel.Main.Services;

internal sealed class GameUpdateMonitorService
(
    MainWindowViewModel vm
)
{
    private static readonly TimeSpan PeriodicCheckInterval = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource cancellationSource = new();

    private TaskCompletionSource checkCompletionSource = CreateCompletedSource();

    private int  isStarted;
    private int  isChecking;
    private int  pendingCheck;
    private int  isUpdateInProgress;
    private long generation;

    public void Start()
    {
        if (Interlocked.Exchange(ref isStarted, 1) != 0)
            return;

        QueueCheck();
        _ = RunPeriodicChecksAsync();
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref isStarted, 0) == 0)
            return;

        Interlocked.Exchange(ref pendingCheck, 0);
        cancellationSource.Cancel();
    }

    public void QueueCheck()
    {
        if (Volatile.Read(ref isStarted) == 0 || cancellationSource.IsCancellationRequested)
            return;

        Volatile.Write(ref pendingCheck, 1);

        if (Volatile.Read(ref isUpdateInProgress) != 0)
            return;

        if (Interlocked.CompareExchange(ref isChecking, 1, 0) == 0)
        {
            var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref checkCompletionSource, completionSource);
            _ = Task.Run(() => ProcessChecksAsync(completionSource));
        }
    }

    public async Task BeginUpdateAsync()
    {
        Interlocked.Exchange(ref isUpdateInProgress, 1);
        Interlocked.Increment(ref generation);

        while (Volatile.Read(ref isChecking) != 0)
        {
            var completionSource = Volatile.Read(ref checkCompletionSource);

            if (completionSource.Task.IsCompleted)
            {
                await Task.Yield();
                continue;
            }

            await completionSource.Task.ConfigureAwait(false);
        }
    }

    public void CompleteUpdate
    (
        bool succeeded
    )
    {
        Interlocked.Increment(ref generation);
        Interlocked.Exchange(ref isUpdateInProgress, 0);

        if (succeeded)
        {
            ApplyAvailability(false);
            return;
        }

        QueueCheck();
    }

    private async Task ProcessChecksAsync
    (
        TaskCompletionSource completionSource
    )
    {
        try
        {
            while (Volatile.Read(ref pendingCheck) != 0)
            {
                if (Volatile.Read(ref isUpdateInProgress) != 0)
                    return;

                if (Interlocked.Exchange(ref pendingCheck, 0) == 0)
                    continue;

                var accountType = vm.CurrentGameLaunchContext?.AccountType ??
                                  vm.AccountManager.CurrentAccount?.AccountType ?? vm.LoginPage.LoginTypeOption.LoginType.ToAccountType(XIVAccountType.Sdo);
                var gamePath = App.Settings.GetGamePath(accountType);
                if (gamePath?.Exists != true)
                    continue;

                var checkGeneration = Volatile.Read(ref generation);

                try
                {
                    if (Volatile.Read(ref isUpdateInProgress) != 0)
                        continue;

                    Log.Information("[GameUpdateMonitor] 正在检查游戏更新");
                    var result = await GameUpdater.Check(gamePath, false, cancellationSource.Token).ConfigureAwait(false);

                    if (checkGeneration != Volatile.Read(ref generation))
                        continue;

                    Log.Information("[GameUpdateMonitor] 更新检查完成, 需要更新 {NeedsUpdate}", result.NeedsUpdate);
                    ApplyAvailability(result.NeedsUpdate);
                }
                catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[GameUpdateMonitor] 检查游戏更新失败");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref isChecking, 0);
            completionSource.TrySetResult();

            if (Volatile.Read(ref pendingCheck)       != 0 &&
                Volatile.Read(ref isUpdateInProgress) == 0 &&
                Volatile.Read(ref isStarted)          != 0)
                QueueCheck();
        }
    }

    private async Task RunPeriodicChecksAsync()
    {
        using var timer = new PeriodicTimer(PeriodicCheckInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationSource.Token).ConfigureAwait(false))
            {
                var dispatcher = vm.Window.Dispatcher;
                if (dispatcher.HasShutdownStarted)
                    return;

                var shouldCheck = dispatcher.Invoke
                (() =>
                     vm.Window.IsVisible && vm.CurrentGameLaunchContext != null
                );

                if (shouldCheck)
                    QueueCheck();
            }
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            Log.Debug("[GameUpdateMonitor] 定时检查已停止");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GameUpdateMonitor] 定时检查异常终止");
        }
    }

    private void ApplyAvailability
    (
        bool needsUpdate
    )
    {
        var dispatcher = vm.Window.Dispatcher;
        if (dispatcher.HasShutdownStarted)
            return;

        if (dispatcher.CheckAccess())
        {
            vm.DashboardPage.IsGameUpdateAvailable = needsUpdate;
            return;
        }

        dispatcher.Invoke(() => vm.DashboardPage.IsGameUpdateAvailable = needsUpdate);
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
