using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Common.Game;
using XIVLauncher.DCTravel;
using XIVLauncher.Login.Models;
using XIVLauncher.Login.WeGame;
using XIVLauncher.Login.Workflow;
using XIVLauncher.Windows.Services;
using XIVLauncher.Windows.ViewModel.Main.Flows;
using XIVLauncher.Windows.ViewModel.Main.Models;
using XIVLauncher.Windows.ViewModel.Main.Services;

namespace XIVLauncher.Windows.ViewModel.Main;

internal partial class MainWindowViewModel : ObservableObject
{
    public SettingsWindowViewModel Settings { get; }

    public LoginPageViewModel LoginPage { get; }

    public InjectPageViewModel InjectPage { get; }

    public DashboardViewModel DashboardPage { get; }

    public DCTravelViewModel DCTravelPage { get; }

    public AccountSwitcherViewModel AccountSwitcher { get; }

    public Launcher       Launcher       { get; private set; }
    public AccountManager AccountManager { get; private set; } = App.AccountManager;
    public Window         Window         { get; private set; }

    public Action         Activate     { get; set; } = null!;
    public Action         Hide         { get; set; } = null!;
    public Action<string> ShowSnackbar { get; set; } = null!;

    [ObservableProperty]
    public partial bool IsLoggingIn { get; set; }

    internal CompanionAppService      CompanionAppService    { get; }
    internal GameInjectionFlow        GameInjectionFlow      { get; }
    internal GameClientFileFlow       GameClientFileFlow     { get; }
    internal DCTravelRuntimeService   DCTravelRuntimeService { get; }
    internal GameUpdateMonitorService GameUpdateMonitor      { get; }

    internal NewsFlow      NewsFlow          { get; }
    internal AccountFlow   AccountFlow       { get; }
    internal StartupFlow   StartupFlow       { get; }
    public DalamudStatusViewModel DalamudStatus { get; }

    internal LoginFlow      LoginFlow      { get; }
    internal GameLaunchFlow GameLaunchFlow { get; }
    internal DashboardFlow  DashboardFlow  { get; }

    public GameLaunchContext? CurrentGameLaunchContext { get; set; }

    private LoginCardType injectModeSourceCard = LoginCardType.MainPage;

    public MainWindowViewModel
    (
        Window window
    )
    {
        Window   = window;
        Settings = new(new DialogService(window), new ExternalLaunchService());
        Launcher = new();

        var loginWorkflowService = new LoginWorkflowService(App.AccountManager, new WeGameTokenCaptureCoordinator());

        DCTravelRuntimeService = new
        (name =>
            {
                var matched = CurrentGameLaunchContext?.Areas.FirstOrDefault
                    (area => string.Equals(area.AreaName, name, StringComparison.Ordinal));

                if (matched == null)
                {
                    Log.Warning("[DCTravel] 当前登录上下文中不存在大区 {AreaName}, 忽略同步请求", name);
                    return;
                }

                var account = App.AccountManager.CurrentAccount;

                if (account == null)
                {
                    Log.Warning("[DCTravel] 当前账号不存在, 忽略大区同步请求: {AreaName}", name);
                    return;
                }

                account.AreaName = name;
                App.AccountManager.Save();
                CurrentGameLaunchContext!.Area = matched;
                Log.Information("[DCTravel] 已同步启动上下文大区为 {AreaName} (ID={AreaID})", name, matched.AreaID);

                Window.Dispatcher.Invoke
                (() =>
                    {
                        var uiArea = LoginPage?.LoginAreas.FirstOrDefault
                        (a =>
                             string.Equals(a.AreaName, name, StringComparison.Ordinal)
                        );
                        if (uiArea != null)
                            LoginPage?.Area = uiArea;

                        var dashboardArea = DashboardPage.Areas.FirstOrDefault
                        (a =>
                             string.Equals(a.AreaName, name, StringComparison.Ordinal)
                        );
                        if (dashboardArea != null)
                            DashboardPage.SelectedArea = dashboardArea;
                    }
                );
            }
        );
        var dalamudLaunchService = new DalamudLaunchService(window);
        CompanionAppService = new();
        GameInjectionFlow   = new(window, dalamudLaunchService);
        GameClientFileFlow  = new GameClientFileFlow(window);

        LoginFlow      = new(this, loginWorkflowService, DCTravelRuntimeService, GameClientFileFlow);
        GameLaunchFlow = new(this, CompanionAppService, GameClientFileFlow, dalamudLaunchService);
        DashboardFlow  = new(this);
        NewsFlow       = new(this);
        AccountFlow    = new(this);
        StartupFlow    = new(this);
        DalamudStatus  = new(window, Settings);

        AccountSwitcher = new AccountSwitcherViewModel
        (
            AccountManager,
            new DialogService(window),
            new ShortcutService(),
            CloseAccountSwitcher
        );
        AccountSwitcher.AccountRemoved += OnAccountRemoved;

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
            GameInjectionFlow,
            CompanionAppService,
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
            DashboardFlow.HandleOpenAuthenticatedSiteAsync,
            DashboardFlow.HandleSetAreaFromDashboard
        );
        GameUpdateMonitor = new GameUpdateMonitorService(this);

        DCTravelPage = new DCTravelViewModel
        (
            () => SwitchCard(LoginCardType.Dashboard),
            () => SwitchCard(LoginCardType.DCTravelHistory),
            () => SwitchCard(LoginCardType.DCTravel),
            () => SwitchCard(LoginCardType.DCTravelProgress),
            () => SwitchCard(LoginCardType.DCTravelReturn),
            DashboardFlow.HandleSetCurrentAreaFromDCTravel,
            () => Activate(),
            () => Window.Dispatcher.Invoke
            (() =>
                {
                    SwitchCard(LoginCardType.Dashboard, false);
                    DashboardFlow.HandleStartGameFromDashboard(LoginAfterAction.Start);
                }
            ),
            () => DCTravelRuntimeService.Client
        )
        {
            AutoStartGameOnComplete = App.Settings.DCTravelAutoStartGameOnComplete
        };

        DCTravelPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DCTravelPage.AutoStartGameOnComplete))
                App.Settings.DCTravelAutoStartGameOnComplete = DCTravelPage.AutoStartGameOnComplete;
        };

        DalamudStatus.RefreshStatus();

        DCTravelRuntimeService.MaintenanceStateChanged += state =>
        {
            Window.Dispatcher.Invoke
            (() =>
                {
                    var isMaintenance = state == DCTravelMaintenanceState.UnderMaintenance;
                    DCTravelPage.IsUnderMaintenance = isMaintenance;
                    DCTravelPage.MaintenanceMessage = isMaintenance ?
                                                          "超域旅行服务维护中, 请稍后再试" :
                                                          string.Empty;
                    DashboardPage.IsDCTravelUnderMaintenance = isMaintenance;
                }
            );
        };

        Settings.SettingsSaved += (_, _) =>
        {
            InjectPage.ReloadSettings();
            DalamudStatus.RefreshCommandState();
        };
        Settings.SettingsSaved += (_, _) => NewsFlow.RefreshNow();
    }

    public void CloseAccountSwitcher() =>
        IsAccountSwitcherOpen = false;

    private void OnAccountRemoved
    (
        string removedUserName
    ) =>
        Window.Dispatcher.Invoke
        (() =>
            {
                if (string.Equals(LoginPage.Username, removedUserName, StringComparison.Ordinal))
                    LoginPage.Username = string.Empty;
            }
        );

    #region 界面控制

    public void SwitchCard
    (
        LoginCardType i,
        bool          shouldCancelLogin = true
    ) =>
        Window.Dispatcher.Invoke
        (() =>
            {
                if (shouldCancelLogin)
                    CancelLogin();

                if (i == LoginCardType.InjectMode)
                {
                    var currentCard = (LoginCardType)LoginCardTransitionerIndex;
                    if (currentCard != LoginCardType.InjectMode && currentCard != LoginCardType.Logining) injectModeSourceCard = currentCard;

                    InjectPage.ReturnButtonText = injectModeSourceCard == LoginCardType.Dashboard ?
                                                      "返回主页面" :
                                                      "返回账号登录";
                }

                LoginCardTransitionerIndex = (int)i;

                InjectPage.SetActive(LoginCardTransitionerIndex == (int)LoginCardType.InjectMode);
            }
        );

    /// <summary>
    ///     取消当前登录流程, 委托至 LoginFlow。
    /// </summary>
    public void CancelLogin() =>
        LoginFlow.CancelLogin();

    [RelayCommand]
    private void AccountSwitcherButton() =>
        SwitchCard(LoginCardType.AccountSwitcher);

    #endregion

    #region Loading 弹窗

    private void ShowLoadingDialog
    (
        string message
    )
    {
        IsLoadingDialogOpen  = true;
        LoadingDialogMessage = message;
    }

    private void HideLoadingDialog() =>
        IsLoadingDialogOpen = false;

    #endregion

    #region 事件

    public void OnWindowClosed
    (
        object? sender,
        object  args
    )
    {
        DalamudStatus.Detach();
        GameUpdateMonitor.Stop();
        InjectPage.StopRefreshing(true);
        CancelLogin();
        Application.Current.Shutdown();
    }

    public void OnWindowClosing
    (
        object?         sender,
        CancelEventArgs args
    )
    {
        if (!IsLoggingIn) return;
        args.Cancel = true;
    }

    #endregion

    #region Bindings

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial int LoginCardTransitionerIndex { get; set; } = 1;

    [ObservableProperty]
    public partial bool IsLoadingDialogOpen { get; set; }

    [ObservableProperty]
    public partial bool IsAccountSwitcherOpen { get; set; }

    [ObservableProperty]
    public partial string LoadingDialogMessage { get; set; } = string.Empty;

    #endregion
}
