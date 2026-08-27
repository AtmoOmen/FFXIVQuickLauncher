using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Util;
using XIVLauncher.Windows.ViewModel.Main.Flows;
using XIVLauncher.Windows.ViewModel.Main.Services;

namespace XIVLauncher.Windows.ViewModel.Main;

public sealed partial class InjectPageViewModel : ObservableObject
{
    private readonly Window                  window;
    private readonly GameInjectionFlow       gameInjectionFlow;
    private readonly CompanionAppService     companionAppService;
    private readonly SettingsWindowViewModel settings;
    private readonly Func<bool>              isLoggingInFunc;
    private readonly Action<string>          showLoadingDialogAction;
    private readonly Action                  hideLoadingDialogAction;
    private readonly Action                  activateWindowAction;
    private readonly Action                  requestReturnToLoginPageAction;
    private readonly HashSet<int>            autoInjectAttemptedProcessIds = [];

    private CancellationTokenSource? processRefreshCancelSource;
    private CancellationTokenSource? autoInjectDelayCancelSource;
    private Task?                    processRefreshTask;
    private int?                     pendingAutoInjectProcessId;

    public InjectPageViewModel
    (
        Window                  window,
        GameInjectionFlow       gameInjectionFlow,
        CompanionAppService     companionAppService,
        SettingsWindowViewModel settings,
        Func<bool>              isLoggingInFunc,
        Action<string>          showLoadingDialogAction,
        Action                  hideLoadingDialogAction,
        Action                  activateWindowAction,
        Action                  requestReturnToLoginPageAction
    )
    {
        this.window                         = window;
        this.gameInjectionFlow              = gameInjectionFlow;
        this.companionAppService            = companionAppService;
        this.settings                       = settings;
        this.isLoggingInFunc                = isLoggingInFunc;
        this.showLoadingDialogAction        = showLoadingDialogAction;
        this.hideLoadingDialogAction        = hideLoadingDialogAction;
        this.activateWindowAction           = activateWindowAction;
        this.requestReturnToLoginPageAction = requestReturnToLoginPageAction;

        FFXIVProcesses.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAvailableProcesses));
            OnPropertyChanged(nameof(ProcessSelectionHint));
            InjectGameCommand.NotifyCanExecuteChanged();
            BringProcessForegroundCommand.NotifyCanExecuteChanged();
        };

        ReloadSettings();
    }

    [ObservableProperty]
    public partial string ReturnButtonText { get; set; } = "返回账号登录";

    public ObservableCollection<FFXIVProcess> FFXIVProcesses { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InjectGameCommand))]
    [NotifyCanExecuteChangedFor(nameof(BringProcessForegroundCommand))]
    [NotifyPropertyChangedFor(nameof(CanOperateOnSelectedProcess))]
    public partial FFXIVProcess? SelectedProcess { get; set; }

    [ObservableProperty]
    public partial bool AutoInjectEnabled { get; set; }

    partial void OnAutoInjectEnabledChanged
    (
        bool value
    )
    {
        App.Settings.ManualInjectAutoInjectEnabled = value;

        if (!value)
        {
            CancelPendingAutoInject();
            autoInjectAttemptedProcessIds.Clear();
        }

        SyncAutoInjectState();
    }

    [ObservableProperty]
    public partial decimal? ManualInjectDelayMs { get; set; }

    partial void OnManualInjectDelayMsChanged
    (
        decimal? value
    )
    {
        App.Settings.ManualInjectDelayMs = value ?? 0;
        settings.ManualInjectDelayMs     = value;
        SyncAutoInjectState();
    }

    public bool HasAvailableProcesses => FFXIVProcesses.Count > 0;

    public bool CanOperateOnSelectedProcess => SelectedProcess is { HasInjected: false };

    public string ProcessSelectionHint => HasAvailableProcesses ?
                                              "选择要注入的进程" :
                                              "未检测到游戏进程";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InjectGameCommand))]
    public partial bool IsInjecting { get; private set; }

    partial void OnIsInjectingChanged
    (
        bool value
    ) =>
        SyncAutoInjectState();

    public void ReloadSettings()
    {
        AutoInjectEnabled   = App.Settings.ManualInjectAutoInjectEnabled;
        ManualInjectDelayMs = App.Settings.ManualInjectDelayMs;
    }

    public void SetActive
    (
        bool isActive
    )
    {
        if (isActive)
        {
            StartRefreshFFXIVProcess();
            return;
        }

        StopRefreshFFXIVProcess(true);
    }

    public void StopRefreshing
    (
        bool clearCollection
    ) =>
        StopRefreshFFXIVProcess(clearCollection);

    public void RefreshCommandStates()
    {
        InjectGameCommand.NotifyCanExecuteChanged();
        BringProcessForegroundCommand.NotifyCanExecuteChanged();
        ReturnToLoginPageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanInjectGame))]
    private void InjectGame() =>
        StartInject(SelectedProcess, false);

    private bool CanInjectGame() =>
        !isLoggingInFunc() && !IsInjecting && CanOperateOnSelectedProcess;

    [RelayCommand(CanExecute = nameof(CanBringProcessForeground))]
    private void BringProcessForeground()
    {
        if (SelectedProcess != null)
            PlatformHelpers.BringProcessForeground(SelectedProcess.ProcessID);
    }

    private bool CanBringProcessForeground() =>
        SelectedProcess != null;

    [RelayCommand(CanExecute = nameof(CanReturnToLoginPage))]
    private void ReturnToLoginPage() =>
        requestReturnToLoginPageAction();

    private bool CanReturnToLoginPage() =>
        !isLoggingInFunc();

    private void StartInject
    (
        FFXIVProcess? targetProcess,
        bool          isAutoInjection
    )
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

                    if (!gameInjectionFlow.InjectGame(targetProcess.ProcessID))
                        return;

                    companionAppService.StartCompanionAppsUntilGameExit(targetProcess.ProcessID);

                    window.Dispatcher.Invoke
                    (() =>
                        {
                            targetProcess.HasInjected = true;
                            OnPropertyChanged(nameof(CanOperateOnSelectedProcess));
                            InjectGameCommand.NotifyCanExecuteChanged();
                        }
                    );

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
        AutoInjectEnabled && !isLoggingInFunc() && !IsInjecting;

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
                            if (process is not { HasInjected: false })
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

                                var nextSelectedProcess = selectedProcessId.HasValue ?
                                                              FFXIVProcesses.FirstOrDefault(p => p.ProcessID == selectedProcessId.Value) :
                                                              SelectedProcess;

                                SelectedProcess = nextSelectedProcess ?? FFXIVProcesses.FirstOrDefault();
                                OnPropertyChanged(nameof(CanOperateOnSelectedProcess));
                                InjectGameCommand.NotifyCanExecuteChanged();
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

    private void StopRefreshFFXIVProcess
    (
        bool clearCollection
    )
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
}
