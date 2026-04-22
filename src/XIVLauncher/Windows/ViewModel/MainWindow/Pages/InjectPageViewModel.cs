using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Serilog;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Util;
using XIVLauncher.Windows.ViewModel.MainWindow.Services;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Pages;

public sealed class InjectPageViewModel : ViewModelBase
{
    private readonly Window                   window;
    private readonly GameLaunchService        gameLaunchService;
    private readonly SettingsWindowViewModel settings;
    private readonly Func<bool>               isLoggingInFunc;
    private readonly Action<string>           showLoadingDialogAction;
    private readonly Action                   hideLoadingDialogAction;
    private readonly Action                   activateWindowAction;
    private readonly HashSet<int>             autoInjectAttemptedProcessIds = [];
    private readonly SyncCommand              injectGameCommand;
    private readonly SyncCommand              bringProcessForegroundCommand;
    private readonly SyncCommand              returnToLoginPageCommand;

    private CancellationTokenSource? processRefreshCancelSource;
    private CancellationTokenSource? autoInjectDelayCancelSource;
    private Task?                    processRefreshTask;
    private int?                     pendingAutoInjectProcessId;

    public InjectPageViewModel
    (
        Window                   window,
        GameLaunchService        gameLaunchService,
        SettingsWindowViewModel settings,
        Func<bool>               isLoggingInFunc,
        Action<string>           showLoadingDialogAction,
        Action                   hideLoadingDialogAction,
        Action                   activateWindowAction,
        Action                   requestReturnToLoginPageAction
    )
    {
        this.window                  = window;
        this.gameLaunchService       = gameLaunchService;
        this.settings                = settings;
        this.isLoggingInFunc         = isLoggingInFunc;
        this.showLoadingDialogAction = showLoadingDialogAction;
        this.hideLoadingDialogAction = hideLoadingDialogAction;
        this.activateWindowAction    = activateWindowAction;

        FFXIVProcesses.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAvailableProcesses));
            OnPropertyChanged(nameof(ProcessSelectionHint));
            injectGameCommand.RaiseCanExecuteChanged();
            bringProcessForegroundCommand.RaiseCanExecuteChanged();
        };

        injectGameCommand = new SyncCommand(_ => StartInject(SelectedProcess, false), () => !this.isLoggingInFunc() && !IsInjecting && SelectedProcess != null);
        bringProcessForegroundCommand = new SyncCommand
        (
            _ =>
            {
                if (SelectedProcess != null)
                    PlatformHelpers.BringProcessForeground(SelectedProcess.ProcessID);
            },
            () => SelectedProcess != null
        );
        returnToLoginPageCommand = new SyncCommand(_ => requestReturnToLoginPageAction(), () => !this.isLoggingInFunc());

        ReloadSettings();
    }

    public ICommand InjectGameCommand => injectGameCommand;

    public ICommand BringProcessForegroundCommand => bringProcessForegroundCommand;

    public ICommand ReturnToLoginPageCommand => returnToLoginPageCommand;

    public ObservableCollection<FFXIVProcess> FFXIVProcesses { get; } = [];

    public bool AutoInjectEnabled
    {
        get => autoInjectEnabled;
        set
        {
            if (!SetProperty(ref autoInjectEnabled, value))
                return;

            App.Settings.ManualInjectAutoInjectEnabled = value;

            if (!value)
            {
                CancelPendingAutoInject();
                autoInjectAttemptedProcessIds.Clear();
            }

            SyncAutoInjectState();
        }
    }

    public decimal? ManualInjectDelayMs
    {
        get => manualInjectDelayMs;
        set
        {
            if (!SetProperty(ref manualInjectDelayMs, value))
                return;

            App.Settings.ManualInjectDelayMs = value ?? 0;
            settings.ManualInjectDelayMs     = value;
            SyncAutoInjectState();
        }
    }

    public FFXIVProcess? SelectedProcess
    {
        get => selectedProcess;
        set
        {
            if (ReferenceEquals(selectedProcess, value))
                return;

            selectedProcess = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanOperateOnSelectedProcess));
            injectGameCommand.RaiseCanExecuteChanged();
            bringProcessForegroundCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasAvailableProcesses => FFXIVProcesses.Count > 0;

    public bool CanOperateOnSelectedProcess => SelectedProcess != null;

    public void ReloadSettings()
    {
        AutoInjectEnabled   = App.Settings.ManualInjectAutoInjectEnabled.GetValueOrDefault(false);
        ManualInjectDelayMs = App.Settings.ManualInjectDelayMs;
    }

    public void SetActive(bool isActive)
    {
        if (isActive)
        {
            StartRefreshFFXIVProcess();
            return;
        }

        StopRefreshFFXIVProcess(true);
    }

    public void StopRefreshing(bool clearCollection) =>
        StopRefreshFFXIVProcess(clearCollection);

    public string ProcessSelectionHint => HasAvailableProcesses ? "选择要注入的进程" : "未检测到可注入进程";

    public void RefreshCommandStates()
    {
        injectGameCommand.RaiseCanExecuteChanged();
        bringProcessForegroundCommand.RaiseCanExecuteChanged();
        returnToLoginPageCommand.RaiseCanExecuteChanged();
    }

    private bool IsInjecting
    {
        get => isInjecting;
        set
        {
            if (!SetProperty(ref isInjecting, value))
                return;

            injectGameCommand.RaiseCanExecuteChanged();
            SyncAutoInjectState();
        }
    }

    private void StartInject(FFXIVProcess? targetProcess, bool isAutoInjection)
    {
        if (!window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.Invoke(() => StartInject(targetProcess, isAutoInjection));
            return;
        }

        if (IsInjecting || targetProcess == null)
            return;

        CancelPendingAutoInject();

        if (!isAutoInjection)
            showLoadingDialogAction("注入中...");

        IsInjecting = true;

        Task.Run
        (() =>
            {
                try
                {
                    if (targetProcess.HasInjected)
                    {
                        if (isAutoInjection)
                            return;

                        CustomMessageBox.Builder
                                        .NewFrom("选定进程已被注入")
                                        .WithButtons(MessageBoxButton.OK)
                                        .WithCaption("XIVLauncherCN (Soil)")
                                        .WithParentWindow(window)
                                        .Show();
                        return;
                    }

                    if (!gameLaunchService.InjectGameAndAddon(targetProcess.ProcessID))
                        return;

                    window.Dispatcher.Invoke(() => { targetProcess.HasInjected = true; });

                    if (isAutoInjection)
                        return;

                    var dialog = CustomMessageBox.Builder
                                                 .NewFrom("注入完成, 是否要退出 XIVLauncherCN")
                                                 .WithButtons(MessageBoxButton.YesNo)
                                                 .WithCaption("XIVLauncherCN (Soil)")
                                                 .WithParentWindow(window)
                                                 .Show();

                    if (dialog == MessageBoxResult.Yes)
                    {
                        Log.CloseAndFlush();
                        Environment.Exit(0);
                    }
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Builder
                                    .NewFromUnexpectedException(ex, "InjectGame")
                                    .WithParentWindow(window)
                                    .Show();
                }
                finally
                {
                    window.Dispatcher.Invoke
                    (() =>
                        {
                            hideLoadingDialogAction();
                            IsInjecting = false;

                            if (!isAutoInjection)
                                activateWindowAction();
                        }
                    );
                }
            }
        );
    }

    private void CancelPendingAutoInject()
    {
        if (autoInjectDelayCancelSource == null)
            return;

        autoInjectDelayCancelSource.Cancel();
        autoInjectDelayCancelSource.Dispose();
        autoInjectDelayCancelSource = null;
        pendingAutoInjectProcessId  = null;
    }

    private void CleanupAutoInjectAttemptedProcesses()
        => AutoInjectProcessSelector.CleanupAttemptedProcessIds(FFXIVProcesses, autoInjectAttemptedProcessIds);

    private bool CanAutoInject() =>
        AutoInjectEnabled
        && !isLoggingInFunc()
        && !IsInjecting;

    private void SyncAutoInjectState()
    {
        if (!CanAutoInject())
        {
            CancelPendingAutoInject();
            return;
        }

        var candidate = AutoInjectProcessSelector.FindNextCandidate(FFXIVProcesses, autoInjectAttemptedProcessIds);

        if (candidate == null)
        {
            CancelPendingAutoInject();
            return;
        }

        if (pendingAutoInjectProcessId == candidate.ProcessID)
            return;

        CancelPendingAutoInject();
        pendingAutoInjectProcessId  = candidate.ProcessID;
        autoInjectDelayCancelSource = new CancellationTokenSource();

        var autoInjectToken = autoInjectDelayCancelSource.Token;
        var delayMs         = Math.Max((int)ManualInjectDelayMs.GetValueOrDefault(0), 0);

        Task.Run
        (
            async () =>
            {
                try
                {
                    if (delayMs > 0)
                        await Task.Delay(delayMs, autoInjectToken);

                    if (autoInjectToken.IsCancellationRequested)
                        return;

                    window.Dispatcher.Invoke
                    (() =>
                        {
                            if (pendingAutoInjectProcessId != candidate.ProcessID || !CanAutoInject())
                                return;

                            var process = FFXIVProcesses.FirstOrDefault(p => p.ProcessID == candidate.ProcessID);
                            if (process == null || process.HasInjected)
                                return;

                            autoInjectAttemptedProcessIds.Add(process.ProcessID);
                            SelectedProcess = process;
                            StartInject(process, true);
                        }
                    );
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    if (pendingAutoInjectProcessId == candidate.ProcessID)
                        pendingAutoInjectProcessId = null;
                }
            },
            autoInjectToken
        );
    }

    private void StartRefreshFFXIVProcess()
    {
        if (processRefreshTask is { IsCompleted: false })
            return;

        processRefreshCancelSource?.Dispose();
        processRefreshCancelSource = new();

        var processRefreshToken = processRefreshCancelSource.Token;
        processRefreshTask = Task.Run
        (
            async () =>
            {
                try
                {
                    while (!processRefreshToken.IsCancellationRequested)
                    {
                        var newProcesses = FFXIVProcess.GetGameProcess();
                        Application.Current.Dispatcher.Invoke
                        (() =>
                            {
                                var selectedProcessId  = SelectedProcess?.ProcessID;
                                var incomingProcessMap = newProcesses.ToDictionary(p => p.ProcessID);

                                for (var i = FFXIVProcesses.Count - 1; i >= 0; i--)
                                {
                                    var existingProcess = FFXIVProcesses[i];

                                    if (incomingProcessMap.TryGetValue(existingProcess.ProcessID, out var duplicateProcess))
                                    {
                                        existingProcess.HasInjected = duplicateProcess.HasInjected;
                                        duplicateProcess.Dispose();
                                        incomingProcessMap.Remove(existingProcess.ProcessID);
                                        continue;
                                    }

                                    existingProcess.Dispose();
                                    FFXIVProcesses.RemoveAt(i);
                                }

                                foreach (var process in incomingProcessMap.Values)
                                    FFXIVProcesses.Add(process);

                                var nextSelectedProcess = selectedProcessId.HasValue
                                                              ? FFXIVProcesses.FirstOrDefault(p => p.ProcessID == selectedProcessId.Value)
                                                              : SelectedProcess;

                                SelectedProcess = nextSelectedProcess ?? FFXIVProcesses.FirstOrDefault();
                                CleanupAutoInjectAttemptedProcesses();
                                SyncAutoInjectState();
                            }
                        );

                        Log.Verbose("Refreshing Processes...");
                        await Task.Delay(1000, processRefreshToken);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            processRefreshToken
        );
    }

    private void StopRefreshFFXIVProcess(bool clearCollection)
    {
        CancelPendingAutoInject();

        if (processRefreshCancelSource != null)
        {
            processRefreshCancelSource.Cancel();
            processRefreshCancelSource.Dispose();
            processRefreshCancelSource = null;
        }

        processRefreshTask = null;

        if (!clearCollection)
            return;

        foreach (var process in FFXIVProcesses)
            process.Dispose();

        autoInjectAttemptedProcessIds.Clear();
        FFXIVProcesses.Clear();
        SelectedProcess = null;
    }

    private bool          autoInjectEnabled;
    private decimal?      manualInjectDelayMs;
    private FFXIVProcess? selectedProcess;
    private bool          isInjecting;
}
