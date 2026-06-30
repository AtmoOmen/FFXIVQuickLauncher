using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Common.Game;
using XIVLauncher.Dalamud;
using XIVLauncher.DCTravel;
using XIVLauncher.Login;
using XIVLauncher.Windows.Services;
using XIVLauncher.Windows.ViewModel.Main.Handlers;
using XIVLauncher.Windows.ViewModel.Main.Models;
using XIVLauncher.Windows.ViewModel.Main.Providers;
using XIVLauncher.Windows.ViewModel.Main.Services;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel.Main;

internal class MainWindowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    
    public SettingsWindowViewModel Settings { get; }

    public LoginPageViewModel LoginPage { get; }

    public InjectPageViewModel InjectPage { get; }

    public DashboardViewModel DashboardPage { get; }

    public DCTravelViewModel DCTravelPage { get; }

    public ICommand AccountSwitcherButtonCommand { get; }
    
    public ICommand RefreshDalamudInfoCommand { get; }

    public AccountSwitcherViewModel AccountSwitcher { get; }

    public Launcher       Launcher       { get; private set; }
    public AccountManager AccountManager { get; private set; } = App.AccountManager;
    public Window         Window         { get; private set; }

    public Action         Activate        { get; set; } = null!;
    public Action         Hide            { get; set; } = null!;
    public Action         ReloadHeadlines { get; set; } = null!;
    public Action<string> ShowSnackbar    { get; set; } = null!;

    public Action? RequestSwitchToCurrentAccount { get; set; }

    public bool IsLoggingIn { get; set; }

    internal MainWindowDialogProvider  DialogProvider            { get; }
    internal DCTravelRuntimeService    DCTravelRuntimeService    { get; }
    internal GameLaunchService         GameLaunchService         { get; }
    internal GameClientFileTaskService GameClientFileTaskService { get; }

    internal LoginFlowHandler      LoginFlow      { get; }
    internal GameLaunchFlowHandler GameLaunchFlow { get; }
    internal DashboardFlowHandler  DashboardFlow  { get; }
    
    public GameLaunchContext? CurrentGameLaunchContext { get; set; }
    
    private LoginCardType injectModeSourceCard = LoginCardType.MainPage;

    public MainWindowViewModel(Window window)
    {
        Window               = window;
        Settings             = new(new DialogService(window), new ExternalLaunchService());
        DialogProvider       = new(window);
        Launcher             = new();

        var loginWorkflowService = new LoginWorkflowService(App.AccountManager, new WeGameTokenCaptureCoordinator());

        DCTravelRuntimeService = new
        (name =>
            {
                App.AccountManager.CurrentAccount!.AreaName = name;
                App.AccountManager.Save();

                if (CurrentGameLaunchContext?.Areas is { } areas)
                {
                    var matched = areas.FirstOrDefault
                    (a =>
                         string.Equals(a.AreaName, name, StringComparison.Ordinal)
                    );

                    if (matched != null)
                    {
                        CurrentGameLaunchContext.Area = matched;
                        Log.Information("[DCTravel] 已同步启动上下文大区为 {AreaName} (ID={AreaID})", name, matched.AreaID);
                    }
                    else Log.Warning("[DCTravel] 无法从大区列表中找到 \"{AreaName}\"，AreaID/Lobby 等参数未更新", name);
                }

                Window.Dispatcher.Invoke
                (() =>
                    {
                        var uiArea = LoginPage?.LoginAreas.FirstOrDefault
                        (a =>
                             string.Equals(a.AreaName, name, StringComparison.Ordinal)
                        );
                        if (uiArea != null)
                            LoginPage?.Area = uiArea;
                    }
                );
            }
        );
        GameLaunchService         = new GameLaunchService(window);
        GameClientFileTaskService = new GameClientFileTaskService(window);

        LoginFlow      = new(this, loginWorkflowService, DialogProvider, DCTravelRuntimeService, GameClientFileTaskService);
        GameLaunchFlow = new(this, GameLaunchService, GameClientFileTaskService);
        DashboardFlow  = new(this);

        AccountSwitcher = new AccountSwitcherViewModel
        (
            AccountManager,
            new DialogService(window),
            new ShortcutService(),
            CloseAccountSwitcher
        );
        AccountSwitcher.AccountRemoved += OnAccountRemoved;

        AccountSwitcherButtonCommand = new SyncCommand(ExecuteAccountSwitcherButton);

        RefreshDalamudInfoCommand = new SyncCommand
        (
            _ => RefreshDalamudInfo(),
            () => Settings.EnableHooks && App.Dalamud.Updater.State != DalamudUpdater.DownloadState.Unknown
        );

        LoginPage = new LoginPageViewModel
        (
            () => IsLoggingIn,
            LoginFlow.HandleLoginAction,
            LoginFlow.HandleGameClientFileTask,
            CancelLogin,
            LoginFlow.RefreshQrCode,
            () => SwitchCard(LoginCardType.InjectMode),
            () => SwitchCard(LoginCardType.MainPage),
            GameLaunchFlow.FakeStartGame
        );

        InjectPage = new InjectPageViewModel
        (
            window,
            GameLaunchService,
            Settings,
            () => IsLoggingIn,
            ShowLoadingDialog,
            HideLoadingDialog,
            () => Activate(),
            () => SwitchCard(injectModeSourceCard)
        );

        LoginPage.RefreshCommandStates();
        InjectPage.RefreshCommandStates();

        DashboardPage = new DashboardViewModel
        (
            DashboardFlow.HandleStartGameFromDashboard,
            DashboardFlow.HandleSwitchAccount,
            DashboardFlow.HandleOpenDCTravel,
            DashboardFlow.HandleOpenDeviceProfile,
            DashboardFlow.HandleSetAreaFromDashboard
        );

        DCTravelPage = new DCTravelViewModel
        (
            () => SwitchCard(LoginCardType.Dashboard),
            () => SwitchCard(LoginCardType.DCTravelHistory),
            () => SwitchCard(LoginCardType.DCTravel),
            () => SwitchCard(LoginCardType.DCTravelProgress),
            () => SwitchCard(LoginCardType.DCTravelReturn),
            DashboardFlow.HandleSetCurrentAreaFromDCTravel,
            () => Activate(),
            () => DCTravelRuntimeService.Client
        );

        UpdateDalamudStatusText();

        DCTravelRuntimeService.MaintenanceStateChanged += state =>
        {
            Window.Dispatcher.Invoke
            (() =>
                {
                    var isMaintenance = state == DCTravelMaintenanceState.UnderMaintenance;
                    DCTravelPage.IsUnderMaintenance          = isMaintenance;
                    DCTravelPage.MaintenanceMessage          = isMaintenance ? "超域旅行服务维护中, 请稍后再试" : string.Empty;
                    DashboardPage.IsDCTravelUnderMaintenance = isMaintenance;
                }
            );
        };

        App.Dalamud.StatusChanged += DalamudUpdaterStatusChanged;
        Settings.SettingsSaved += (_, _) =>
        {
            InjectPage.ReloadSettings();
            RefreshDalamudInfoCommandState();
        };
    }

    public void CloseAccountSwitcher() =>
        IsAccountSwitcherOpen = false;

    private void OnAccountRemoved(string removedUserName) =>
        Window.Dispatcher.Invoke
        (() =>
            {
                if (string.Equals(LoginPage.Username, removedUserName, StringComparison.Ordinal))
                    LoginPage.Username = string.Empty;
            }
        );

    #region 界面控制

    public void SwitchCard(LoginCardType i, bool shouldCancelLogin = true) =>
        Window.Dispatcher.Invoke
        (() =>
            {
                if (shouldCancelLogin)
                    CancelLogin();

                if (i == LoginCardType.InjectMode)
                {
                    var currentCard = (LoginCardType)LoginCardTransitionerIndex;
                    if (currentCard != LoginCardType.InjectMode && currentCard != LoginCardType.Logining) injectModeSourceCard = currentCard;

                    InjectPage.ReturnButtonText = injectModeSourceCard == LoginCardType.Dashboard ? "返回主页面" : "返回账号登录";
                }

                LoginCardTransitionerIndex = (int)i;

                InjectPage.SetActive(LoginCardTransitionerIndex == (int)LoginCardType.InjectMode);
            }
        );

    /// <summary>
    ///     取消当前登录流程, 委托至 LoginFlowHandler。
    /// </summary>
    public void CancelLogin() =>
        LoginFlow.CancelLogin();

    private void ExecuteAccountSwitcherButton(object parameter) =>
        SwitchCard(LoginCardType.AccountSwitcher);

    #endregion

    #region Loading 弹窗

    private void ShowLoadingDialog(string message)
    {
        IsLoadingDialogOpen  = true;
        LoadingDialogMessage = message;
    }

    private void HideLoadingDialog() =>
        IsLoadingDialogOpen = false;

    #endregion

    #region Dalamud 状态

    private void RefreshDalamudInfo() =>
        App.Dalamud.RunUpdater(true);

    private void DalamudUpdaterStatusChanged(DalamudStatusSnapshot _)
    {
        if (Window.Dispatcher == Dispatcher.CurrentDispatcher)
        {
            UpdateDalamudStatusText();
            return;
        }

        Window.Dispatcher.Invoke(UpdateDalamudStatusText);
    }

    private void UpdateDalamudStatusText()
    {
        var updater = App.Dalamud.GetStatusSnapshot();

        DalamudStatusText = updater.State switch
        {
            DalamudUpdater.DownloadState.Done        => string.IsNullOrWhiteSpace(DalamudUpdater.Version) ? "Dalamud 已就绪" : $"Dalamud {DalamudUpdater.Version}",
            DalamudUpdater.DownloadState.NoIntegrity => "Dalamud 加载失败",
            _                                        => GetDalamudLoadingText(updater)
        };

        RefreshDalamudInfoCommandState();
    }

    private void RefreshDalamudInfoCommandState() =>
        (RefreshDalamudInfoCommand as SyncCommand).RaiseCanExecuteChanged();

    private static string GetDalamudLoadingText(DalamudStatusSnapshot updater)
    {
        if (updater.LoadingProgress is { } progress)
            return $"Dalamud 正在加载 {progress.ToString("0.##", CultureInfo.InvariantCulture)}%";

        if (!string.IsNullOrWhiteSpace(updater.LoadingDetail))
            return $"Dalamud {updater.LoadingDetail.TrimEnd('.')}";

        return "Dalamud 正在加载";
    }

    #endregion

    #region 事件

    private void OnPropertyChanged(string propertyName)
    {
        var handler = PropertyChanged;
        handler?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void OnWindowClosed(object? sender, object args)
    {
        App.Dalamud.StatusChanged -= DalamudUpdaterStatusChanged;
        InjectPage.StopRefreshing(true);
        CancelLogin();
        Application.Current.Shutdown();
    }

    public void OnWindowClosing(object? sender, CancelEventArgs args)
    {
        if (!IsLoggingIn) return;
        args.Cancel = true;
    }

    #endregion

    #region Bindings

    public bool IsEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(IsEnabled));
        }
    }

    public int LoginCardTransitionerIndex
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(LoginCardTransitionerIndex));
        }
    } = 1;

    public bool IsLoadingDialogOpen
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(IsLoadingDialogOpen));
        }
    }

    public bool IsAccountSwitcherOpen
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(IsAccountSwitcherOpen));
        }
    }

    public string LoadingDialogMessage
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(LoadingDialogMessage));
        }
    } = string.Empty;

    public string DalamudStatusText
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(DalamudStatusText));
        }
    } = string.Empty;

    #endregion
    
    #region 常量

    public const string PRESUDO_PASSWORD = "********假的密码********";

    #endregion
}
