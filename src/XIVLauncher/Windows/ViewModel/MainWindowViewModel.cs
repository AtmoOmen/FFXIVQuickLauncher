using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Account.DeviceProfiles;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.CompanionApp;
using XIVLauncher.Dalamud;
using XIVLauncher.DCTravel;
using XIVLauncher.GamePatchV3.Models;
using XIVLauncher.GamePatchV3.Update;
using XIVLauncher.Login;
using XIVLauncher.Login.Channels;
using XIVLauncher.Support;
using XIVLauncher.Windows.GameClientFiles;
using XIVLauncher.Windows.Services;
using XIVLauncher.Windows.ViewModel.MainWindow;
using XIVLauncher.Windows.ViewModel.MainWindow.Providers;
using XIVLauncher.Windows.ViewModel.MainWindow.Services;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel;

internal class MainWindowViewModel : INotifyPropertyChanged
{
    public const string PRESUDO_PASSWORD = "********假的密码********";

    public SettingsWindowViewModel Settings { get; }

    public LoginPageViewModel LoginPage { get; }

    public InjectPageViewModel InjectPage { get; }

    public DashboardViewModel DashboardPage { get; }

    public DCTravelViewModel DCTravelPage { get; }

    public ICommand AccountSwitcherButtonCommand { get; }

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

    private MainWindowDialogProvider  DialogProvider            { get; }
    private DCTravelRuntimeService    DcTravelRuntimeService    { get; }
    private GameLaunchService         GameLaunchService         { get; }
    private GameClientFileTaskService GameClientFileTaskService { get; }
    private CancellationTokenSource?  LoginCancelSource         { get; set; }
    private bool                      IsLoginCanceledByUser     { get; set; }
    private LoginCardType?            LoginCardAfterCompletion  { get; set; }

    private readonly LoginWorkflowService loginWorkflowService;
    private readonly SyncCommand          refreshDalamudInfoCommand;

    private GameLaunchContext? currentGameLaunchContext;
    private bool               isStartingGameFromDashboard;
    private bool               isWeGameRetryingAfterThirdPartyFailure;
    private LoginCardType      injectModeSourceCard = LoginCardType.MainPage;

    public MainWindowViewModel(Window window)
    {
        Window               = window;
        Settings             = new(new DialogService(window), new ExternalLaunchService());
        DialogProvider       = new(window);
        Launcher             = new();
        loginWorkflowService = new(App.AccountManager, new WeGameTokenCaptureCoordinator());
        DcTravelRuntimeService = new
        (name =>
            {
                App.AccountManager.CurrentAccount!.AreaName = name;
                App.AccountManager.Save();

                if (currentGameLaunchContext?.Areas is { } areas)
                {
                    var matched = areas.FirstOrDefault
                    (a =>
                         string.Equals(a.AreaName, name, StringComparison.Ordinal)
                    );

                    if (matched != null)
                    {
                        currentGameLaunchContext.Area = matched;
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
        AccountSwitcher = new AccountSwitcherViewModel
        (
            AccountManager,
            new DialogService(window),
            new ShortcutService(),
            CloseAccountSwitcher
        );
        AccountSwitcher.AccountRemoved += OnAccountRemoved;
        AccountSwitcherButtonCommand   =  new SyncCommand(ExecuteAccountSwitcherButton);
        refreshDalamudInfoCommand = new
        (
            _ => RefreshDalamudInfo(),
            () => Settings.EnableHooks && App.Dalamud.Updater.State != DalamudUpdater.DownloadState.Unknown
        );
        LoginPage = new LoginPageViewModel
        (
            () => IsLoggingIn,
            HandleLoginAction,
            HandleGameClientFileTask,
            CancelLogin,
            RefreshQrCode,
            () => SwitchCard(LoginCardType.InjectMode),
            () => SwitchCard(LoginCardType.MainPage),
            FakeStartGame
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
            HandleStartGameFromDashboard,
            HandleSwitchAccount,
            HandleOpenDCTravel,
            HandleOpenDeviceProfile,
            HandleSetAreaFromDashboard
        );
        DCTravelPage = new DCTravelViewModel
        (
            () => SwitchCard(LoginCardType.Dashboard),
            () => SwitchCard(LoginCardType.DCTravelHistory),
            () => SwitchCard(LoginCardType.DCTravel),
            () => SwitchCard(LoginCardType.DCTravelProgress),
            () => SwitchCard(LoginCardType.DCTravelReturn),
            HandleSetCurrentAreaFromDCTravel,
            () => Activate(),
            () => DcTravelRuntimeService.Client
        );

        UpdateDalamudStatusText();

        DcTravelRuntimeService.MaintenanceStateChanged += state =>
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

    private void ExecuteAccountSwitcherButton(object parameter) =>
        SwitchCard(LoginCardType.AccountSwitcher);

    #endregion

    #region 登录

    public void StartLogin
    (
        LoginType        loginType,
        string           username,
        string           password,
        bool             quickLoginEnabled,
        bool             readWeGameInfo,
        LoginAfterAction action
    )
    {
        if (Window.Dispatcher != Dispatcher.CurrentDispatcher)
        {
            Window.Dispatcher.Invoke(() => StartLogin(loginType, username, password, quickLoginEnabled, readWeGameInfo, action));
            return;
        }

        if (IsLoggingIn)
            return;

        Log.Information("[MainWindow] 尝试开始登录");

        IsLoggingIn               = true;
        IsEnabled                 = false;
        IsLoginCanceledByUser     = false;
        LoginPage.IsQrCodeExpired = false;
        LoginCancelSource?.Dispose();
        LoginCancelSource = new();
        LoginPage.RefreshCommandStates();
        InjectPage.RefreshCommandStates();

        var currentCard = LoginCardAfterCompletion ?? (LoginCardType)LoginCardTransitionerIndex;
        LoginCardAfterCompletion = null;
        SwitchCard(loginType == LoginType.QRCode ? LoginCardType.ScanQRCode : LoginCardType.Logining, false);

        _ = Task.Run
        (async () =>
            {
                try
                {
                    await LoginAsync(loginType, username, password, quickLoginEnabled, readWeGameInfo, action).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Window.Dispatcher.Invoke
                    (() =>
                        {
                            CustomMessageBox.Builder
                                            .NewFromUnexpectedException(ex, "CreateLoginHandler/Task")
                                            .WithParentWindow(Window)
                                            .Show();
                        }
                    );
                }
                finally
                {
                    Window.Dispatcher.Invoke
                    (() =>
                        {
                            LoginCancelSource?.Dispose();
                            LoginCancelSource = null;

                            var nextCard = LoginCardAfterCompletion ?? currentCard;
                            LoginCardAfterCompletion = null;
                            SwitchCard(nextCard, false);

                            IsLoggingIn = false;
                            IsEnabled   = true;
                            LoginPage.RefreshCommandStates();
                            InjectPage.RefreshCommandStates();

                            ReloadHeadlines();
                            Activate();
                        }
                    );
                }
            }
        );
    }

    public void CancelLogin()
    {
        if (LoginCancelSource != null)
        {
            Log.Information("[MainWindow] 取消登录");
            IsLoginCanceledByUser = true;

            if (!LoginCancelSource.IsCancellationRequested)
                LoginCancelSource.Cancel();
        }
    }

    private async Task LoginAsync
    (
        LoginType        loginType,
        string           username,
        string?          inputPassword,
        bool             quickLoginEnabled,
        bool             readWeGameInfo,
        LoginAfterAction action
    )
    {
        if (IsLoginCanceledByUser)
            return;

        ProblemCheck.RunCheck(Window);

        if (!TryResolvePatchPath())
            return;

        if (LoginPage.Area == null || LoginPage.Area.AreaID == "-1")
        {
            CustomMessageBox.Show
            (
                "获取服务器信息失败, 无法登录",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: Window
            );

            return;
        }

        App.Settings.FastLogin = LoginPage.IsFastLogin;
        inputPassword          = inputPassword == PRESUDO_PASSWORD ? string.Empty : inputPassword?.Trim() ?? string.Empty;

        var loginRequest = new LoginWorkflowRequest
        {
            LoginType                            = loginType,
            Username                             = username,
            Password                             = inputPassword,
            QuickLoginEnabled                    = quickLoginEnabled,
            ReadWeGameInfo                       = readWeGameInfo,
            ForceWeGameTokenRecapture            = loginType == LoginType.WeGame && readWeGameInfo,
            Action                               = action,
            CurrentArea                          = LoginPage.Area,
            LoginAreas                           = LoginPage.LoginAreas,
            LoginCancellationTokenSource         = LoginCancelSource ?? new CancellationTokenSource(),
            LoginSessionRefreshSink              = DcTravelRuntimeService,
            Interaction                          = new MainWindowLoginInteraction(Window, LoginPage, DialogProvider),
            RequireDeviceProfileSetupForNewLogin = App.Settings.RequireDeviceProfileSetupForNewLogin
        };

        LoginWorkflowResult? workflowResult = null;

        try
        {
            workflowResult = await loginWorkflowService.ExecuteAsync(loginRequest).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await HandleLoginWorkflowExceptionAsync(ex, loginType, username, workflowResult?.UsedSavedWeGameToken == true, action).ConfigureAwait(false);
            return;
        }

        if (workflowResult == null)
            return;

        if (workflowResult.IsAccountPersisted)
        {
            Window.Dispatcher.Invoke
            (() =>
                {
                    AccountSwitcher.RefreshEntries(AccountManager.CurrentAccountID, false);

                    if (workflowResult.IsNewAccount)
                        ShowSnackbar($"新账号已保存: {AccountManager.CurrentAccount?.DisplayName}");
                }
            );
        }

        var gameLaunchContext = workflowResult.GameLaunchContext;
        currentGameLaunchContext = gameLaunchContext;
        var oAuthLogin = gameLaunchContext.LoginResult.OAuthLogin;

        Log.Information
        (
            "[MainWindow] 登录结果: {State} {Playable}",
            gameLaunchContext.LoginResult.State,
            oAuthLogin?.Playable
        );

        if (workflowResult.RefreshGameSessionIdByQuickLoginFunc != null)
            DcTravelRuntimeService.ConfigureQuickLoginRefresh(workflowResult.RefreshGameSessionIdByQuickLoginFunc);

        gameLaunchContext.DcTravelPort = gameLaunchContext.LoginResult.State == LoginState.Ok
                                             ? await DcTravelRuntimeService.StartAsync(true, false).ConfigureAwait(false)
                                             : 0;

        if (await ProcessLoginResultAsync(gameLaunchContext, action).ConfigureAwait(false))
            Activate();
    }

    private async Task HandleLoginWorkflowExceptionAsync(Exception ex, LoginType loginType, string username, bool usedSavedWeGameToken, LoginAfterAction action)
    {
        Log.Error(ex, "[MainWindow] 尝试登录至游戏失败");

        if (ex is OperationCanceledException && (IsLoginCanceledByUser || LoginCancelSource?.IsCancellationRequested == true))
        {
            Log.Information("[MainWindow] 用户取消了登录操作, 正常返回");
            return;
        }

        await ClearInvalidSavedWeGameTokenAsync(ex, loginType, username, usedSavedWeGameToken).ConfigureAwait(false);

        var msgbox = new CustomMessageBox.Builder()
                     .WithCaption("登录异常")
                     .WithImage(MessageBoxImage.Error)
                     .WithShowHelpLinks()
                     .WithShowDiscordLink()
                     .WithParentWindow(Window);

        if (ex is LoginException sdoLoginEx)
        {
            if (IsLoginCanceledByUser || LoginCancelSource?.IsCancellationRequested == true)
            {
                Log.Information("[MainWindow] 手动取消登录");
                return;
            }

            if (loginType == LoginType.WeGame && sdoLoginEx.ErrorCode == (int)LoginExceptionCode.ThirdPartyVerificationFailed)
            {
                Log.Information("[MainWindow] WeGame 第三方验证失败, 清除 token 后重新抓包");

                if (isWeGameRetryingAfterThirdPartyFailure)
                {
                    Log.Warning("[MainWindow] WeGame 重试抓包后仍验证失败, 不再继续重试");
                    isWeGameRetryingAfterThirdPartyFailure = false;
                }
                else
                {
                    isWeGameRetryingAfterThirdPartyFailure = true;
                    await LoginAsync(loginType, username, null, false, true, action).ConfigureAwait(false);
                    return;
                }
            }

            if (loginType == LoginType.QRCode && sdoLoginEx.Message == "二维码不存在或已过期，请重试")
            {
                Log.Information("[MainWindow] 二维码已过期，等待手动刷新");
                Window.Dispatcher.Invoke(() => LoginPage.IsQrCodeExpired = true);
                LoginCardAfterCompletion = LoginCardType.ScanQRCode;
                return;
            }

            if (sdoLoginEx.ErrorCode is (int)LoginExceptionCode.CaptchaVerificationCanceled or (int)LoginExceptionCode.SafePhoneVerificationCanceled)
            {
                Log.Information("[MainWindow] 用户主动取消登录验证流程: {ErrorCode}", sdoLoginEx.ErrorCode);
                return;
            }

            if (sdoLoginEx.RemoveQuickLoginSecret)
            {
                Log.Information("[MainWindow] 快速登录失败, 清除 SessionKey: {Username}", username);
                var account = AccountManager.Accounts.FirstOrDefault(x => x.UserName == username);

                if (account != null)
                {
                    account.SdoQuickLoginSecret = string.Empty;
                    AccountManager.Save(account);
                }
            }

            new CustomMessageBox.Builder()
                .WithCaption("登录异常")
                .WithImage(MessageBoxImage.Question)
                .WithParentWindow(Window)
                .WithText($"错误: {sdoLoginEx.Message}\n({sdoLoginEx.ErrorCode})")
                .Show();
            return;
        }

        switch (ex)
        {
            case IOException:
                msgbox.WithText("搜寻游戏文件失败, 游戏路径可能设置错误");
                break;

            case InvalidVersionFilesException:
                msgbox.WithText("从游戏文件中读取版本信息失败, 可能需要重新安装或修复游戏文件");
                break;

            case UnsupportedGameVersionException:
            {
                if (App.Settings.GamePath != null && Repository.Ffxiv.IsBaseVer(App.Settings.GamePath))
                {
                    IsLoggingIn = false;
                    _           = HandleGameClientFileTask(GameClientFileTaskKind.FreshInstall);
                    return;
                }

                msgbox.WithText("无法确认当前游戏版本, 请先运行游戏文件修复后重试");
                break;
            }

            case InvalidDataException invalidDataException when invalidDataException.Message.Contains("当前游戏数据版本", StringComparison.Ordinal):
            {
                if (App.Settings.GamePath != null && Repository.Ffxiv.IsBaseVer(App.Settings.GamePath))
                {
                    IsLoggingIn = false;
                    _           = HandleGameClientFileTask(GameClientFileTaskKind.FreshInstall);
                    return;
                }

                msgbox.WithText("无法确认当前游戏版本, 请先运行游戏文件修复后重试");
                break;
            }

            case OAuthLoginException oauthLoginException:
            {
                if (loginType == LoginType.QRCode && oauthLoginException.OAuthErrorMessage == "二维码不存在或已过期，请重试")
                {
                    Log.Information("[MainWindow] 二维码已过期，等待手动刷新");
                    Window.Dispatcher.Invoke(() => LoginPage.IsQrCodeExpired = true);
                    LoginCardAfterCompletion = LoginCardType.ScanQRCode;
                    return;
                }

                LoginPage.LoginMessage = string.Empty;

                if (string.IsNullOrWhiteSpace(oauthLoginException.OAuthErrorMessage))
                    msgbox.WithText("登录账号失败, 请检查用户名和密码");
                else
                {
                    msgbox.WithText
                    (
                        oauthLoginException.OAuthErrorMessage
                                           .Replace("\\r\\n", "\n")
                                           .Replace("\r\n",   "\n")
                    );
                }

                break;
            }

            case HttpRequestException or TaskCanceledException or WebException:
                msgbox.WithText("连接游戏服务器失败, 请稍后重试");
                break;

            case InvalidResponseException iex:
                Log.Error("[MainWindow] 游戏服务器返回无效响应: {Message}\n{Document}", ex.Message, iex.Document);
                msgbox.WithText("服务器返回无效响应, 请稍后重试");
                break;

            default:
                msgbox.WithShowNewGitHubIssue()
                      .WithAppendDescription(ex.ToString())
                      .WithAppendSettingsDescription("Login")
                      .WithAppendText("\n\n")
                      .WithAppendText("请检查登录信息, 并在稍后重试");
                break;
        }

        msgbox.Show();
    }

    private async Task ClearInvalidSavedWeGameTokenAsync
    (
        Exception ex,
        LoginType loginType,
        string    username,
        bool      usedSavedWeGameToken
    )
    {
        if (ex is not LoginException)
            return;

        if (loginType != LoginType.WeGame || !usedSavedWeGameToken)
            return;

        var account = AccountManager.Accounts.FirstOrDefault
        (item => item.AccountType == XIVAccountType.WeGame && string.Equals(item.WeGameLoginAccount, username, StringComparison.Ordinal)
        );

        if (account?.WeGameQuickLoginSecret == null)
            return;

        await Task.Delay(1);

        account.WeGameQuickLoginSecret = null;
        AccountManager.Save(account);
    }

    private bool ConfirmGameClientFileTask(GameClientFileTaskKind kind)
    {
        string message;
        string title;

        switch (kind)
        {
            case GameClientFileTaskKind.Update:
                message = "即将检查游戏更新, 并下载安装可能存在的更新文件, 是否要开始?";
                title   = "更新游戏文件";
                break;

            case GameClientFileTaskKind.Repair:
                message = "即将扫描游戏文件完整性, 并下载安装存在异常的文件、归档多余的文件, 是否要开始?";
                title   = "修复游戏文件";
                break;

            case GameClientFileTaskKind.IntegrityCheck:
                message = "即将检查游戏文件完整性, 是否要开始?";
                title   = "检查游戏完整性";
                break;

            case GameClientFileTaskKind.FreshInstall:
                message = "未检测到游戏安装, 即将下载安装完整游戏, 是否要开始?";
                title   = "安装游戏文件";
                break;

            default:
                return true;
        }

        return CustomMessageBox.Show(message, title, MessageBoxButton.YesNo, parentWindow: Window) != MessageBoxResult.No;
    }

    private bool ConfirmGamePatchInstall()
    {
        if (!App.Settings.AskBeforePatchInstall)
            return true;

        var selfPatchAsk = CustomMessageBox.Show
        (
            "检测到新的游戏补丁\n是否下载更新文件并安装?",
            "XIVLauncherCN (Soil)",
            MessageBoxButton.YesNo,
            parentWindow: Window
        );

        return selfPatchAsk != MessageBoxResult.No;
    }

    private async Task<bool> ProcessLoginResultAsync(GameLaunchContext gameLaunchContext, LoginAfterAction action)
    {
        var loginResult = gameLaunchContext.LoginResult;

        switch (loginResult.State)
        {
            case LoginState.NoService:
                CustomMessageBox.Show
                (
                    "该账号无游戏游玩权限, 请检查当前账号状态",
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    false,
                    false,
                    parentWindow: Window
                );

                return false;

            case LoginState.NoTerms:
                CustomMessageBox.Show
                (
                    "该账号尚未接受游玩使用条款, 请前往官方启动器进行相关操作",
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    showOfficialLauncher: true,
                    parentWindow: Window
                );

                return false;

            case LoginState.NeedsPatchBoot:
                CustomMessageBox.Show
                (
                    "部分游戏文件损坏, 当前无法更新与启动游戏, 请重新安装游戏",
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    parentWindow: Window
                );

                return false;
        }

        if (loginResult.State == LoginState.NeedRetry)
        {
            Log.Error("loginResult.State == NeedRetry");
            CustomMessageBox.Show
            (
                "登录失败, 建议尝试重新扫码登录",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Information,
                false,
                false,
                parentWindow: Window
            );
            return false;
        }

        if (CustomMessageBox.AssertOrShowError
                (loginResult.State == LoginState.Ok, "ProcessLoginResultAsync: loginResult.State should have been Launcher.LoginState.Ok", parentWindow: Window))
            return false;

        // 对齐官方 V3 启动器：首次登录成功后进入 Dashboard，不立即启动游戏。
        // 从 Dashboard 点击「启动游戏」时 isStartingGameFromDashboard 为 true，直接走下面的启动流程。
        if (!isStartingGameFromDashboard)
        {
            // 首次登录成功，进入 Dashboard。
            // 设置 LoginCardAfterCompletion 防止 StartLogin 的 finally 块切回 MainPage。
            LoginCardAfterCompletion = LoginCardType.Dashboard;
            Window.Dispatcher.Invoke
            (() =>
                {
                    UpdateDashboardInfo(loginResult);
                    SwitchCard(LoginCardType.Dashboard, false);
                }
            );
            return true;
        }

        isStartingGameFromDashboard = false;

        // 在启动游戏前检查是否有待安装的补丁
        try
        {
            var checkResult = await GameUpdater.Check(App.Settings.GamePath, false, CancellationToken.None).ConfigureAwait(false);

            if (checkResult.NeedsUpdate)
            {
                if (!ConfirmGamePatchInstall())
                    return false;

                if (!await InstallGamePatchAsync().ConfigureAwait(false))
                {
                    Log.Error("patchSuccess != true");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MainWindow] 启动前补丁检查失败");
            CustomMessageBox.Builder
                            .NewFrom(ex, "启动前补丁检查")
                            .WithAppendText("\n\n检查游戏更新时发生错误，请稍后重试")
                            .WithParentWindow(Window)
                            .Show();
            return false;
        }

        Hide();

        while (true)
        {
            List<Exception> exceptions = [];

            try
            {
                var oauthLogin = loginResult.OAuthLogin;

                if (oauthLogin == null || string.IsNullOrEmpty(oauthLogin.SndaID))
                {
                    Log.Error("[MainWindow] SNDAID 为空，取消登录");
                    CustomMessageBox.Show
                    (
                        "登录异常: SNDAID 为空",
                        "XIVLauncherCN (Soil)",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        showOfficialLauncher: true,
                        parentWindow: Window
                    );
                    return false;
                }

                using var process = await StartGameAndCompanionApp
                                    (
                                        gameLaunchContext,
                                        action == LoginAfterAction.StartWithoutDalamud,
                                        action == LoginAfterAction.StartWithoutThird,
                                        action == LoginAfterAction.StartWithoutPlugins
                                    ).ConfigureAwait(false);

                if (process == null)
                    return false;

                // 正常退出 / 重启
                if (process.ExitCode is not (0 or 0x12345678) && App.Settings.TreatNonZeroExitCodeAsFailure)
                {
                    var message = new CustomMessageBox.Builder()
                                  .WithTextFormatted
                                  (
                                      "游戏异常退出, 是否尝试重新启动?\n\n退出码: 0x{0:X8}",
                                      (uint)process.ExitCode
                                  )
                                  .WithImage(MessageBoxImage.Exclamation)
                                  .WithShowHelpLinks()
                                  .WithShowDiscordLink()
                                  .WithShowNewGitHubIssue()
                                  .WithButtons(MessageBoxButton.YesNoCancel)
                                  .WithDefaultResult(MessageBoxResult.Yes)
                                  .WithCancelResult(MessageBoxResult.No)
                                  .WithYesButtonText("重启")
                                  .WithNoButtonText("关闭")
                                  .WithCancelButtonText("不再询问")
                                  .WithParentWindow(Window)
                                  .Show();

                    switch (message)
                    {
                        case MessageBoxResult.Yes:
                            continue;

                        case MessageBoxResult.No:
                            break;

                        case MessageBoxResult.Cancel:
                            App.Settings.TreatNonZeroExitCodeAsFailure = false;
                            break;
                    }
                }

                return true;
            }
            catch (AggregateException ex)
            {
                Log.Error(ex, "[MainWindow] 尝试处理登录结果时出现一个或多个异常");

                var innerException = ex.Flatten().InnerException;
                if (innerException != null)
                    exceptions.Add(innerException);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] 尝试处理登录结果时出现异常");

                exceptions.Add(ex);
            }

            var builder = new CustomMessageBox.Builder()
                          .WithImage(MessageBoxImage.Error)
                          .WithShowHelpLinks()
                          .WithShowDiscordLink()
                          .WithShowNewGitHubIssue()
                          .WithButtons(MessageBoxButton.YesNo)
                          .WithDefaultResult(MessageBoxResult.No)
                          .WithCancelResult(MessageBoxResult.No)
                          .WithYesButtonText("重试")
                          .WithNoButtonText("关闭")
                          .WithParentWindow(Window);

            List<string>  summaries    = [];
            List<string>  actionables  = [];
            List<string?> descriptions = [];

            foreach (var exception in exceptions)
            {
                switch (exception)
                {
                    case DalamudRunnerException:
                    case GameExitedException:
                        var count = 0;

                        foreach (var processName in new[] { "ffxiv_dx11", "ffxiv" })
                        {
                            foreach (var process in Process.GetProcessesByName(processName))
                            {
                                count++;
                                process.Dispose();
                            }
                        }

                        if (count >= 2)
                        {
                            summaries.Add("默认情况下无法启动两个以上游戏进程");
                            actionables.Add($"请检查是否存在尚未正常关闭的游戏进程 (当前: {count})");
                            descriptions.Add(null);

                            builder.WithButtons(MessageBoxButton.YesNoCancel)
                                   .WithDefaultResult(MessageBoxResult.Yes)
                                   .WithCancelButtonText("终止后重试");
                        }
                        else
                        {
                            summaries.Add("XIVLauncher 无法正确启动游戏");
                            descriptions.Add(null);
                        }

                        builder.WithShowNewGitHubIssue(false);

                        break;

                    case BinaryNotPresentException:
                        summaries.Add("找不到游戏可执行文件");
                        actionables.Add("可能需要重新安装游戏");
                        descriptions.Add(null);
                        break;

                    case IOException:
                        summaries.Add("无法找到游戏文件");
                        summaries.Add("请检查游戏路径设置情况");
                        descriptions.Add(exception.ToString());
                        descriptions.Add(exception.ToString());
                        break;

                    case Win32Exception win32Exception:
                        summaries.Add($"未知错误 (0x{(uint)win32Exception.HResult:X8}: {win32Exception.Message})");
                        descriptions.Add(exception.ToString());
                        break;

                    default:
                        summaries.Add($"未知错误 ({exception.Message})");
                        descriptions.Add(exception.ToString());
                        break;
                }
            }

            if (exceptions.Count == 1)
            {
                var summary     = summaries.ElementAtOrDefault(0) ?? "发生了未知错误";
                var actionable  = actionables.ElementAtOrDefault(0);
                var description = descriptions.ElementAtOrDefault(0) ?? string.Empty;
                var text        = string.IsNullOrWhiteSpace(actionable) ? summary : $"{summary}\n\n{actionable}";

                builder.WithText(text)
                       .WithDescription(description);
            }
            else
            {
                builder.WithText("发生了多个错误");

                for (var i = 0; i < summaries.Count; i++)
                {
                    var summary     = summaries[i];
                    var actionable  = actionables.ElementAtOrDefault(i);
                    var description = descriptions.ElementAtOrDefault(i);

                    builder.WithAppendText($"\n{i + 1}. {summary}");

                    if (!string.IsNullOrWhiteSpace(actionable))
                        builder.WithAppendText($"\n    => {actionable}");
                    if (string.IsNullOrWhiteSpace(description))
                        continue;
                    builder.WithAppendDescription($"########## 异常 {i + 1} ##########\n{description}\n\n");
                }
            }

            if (descriptions.Any(x => x != null))
                builder.WithAppendSettingsDescription("Login");

            switch (builder.Show())
            {
                case MessageBoxResult.Yes:
                    continue;

                case MessageBoxResult.No:
                    return false;

                case MessageBoxResult.Cancel:
                    for (var pass = 0; pass < 8; pass++)
                    {
                        var allKilled = true;

                        foreach (var processName in new[] { "ffxiv_dx11", "ffxiv" })
                        {
                            foreach (var process in Process.GetProcessesByName(processName))
                            {
                                allKilled = false;

                                try
                                {
                                    process.Kill();
                                }
                                catch (Exception ex2)
                                {
                                    Log.Warning(ex2, "结束进程失败 (PID={0}, name={1})", process.Id, process.ProcessName);
                                }
                                finally
                                {
                                    process.Dispose();
                                }
                            }
                        }

                        if (allKilled)
                            break;
                    }

                    Task.Delay(1000).Wait();
                    continue;
            }
        }
    }

    #endregion

    #region 启动游戏

    public async Task<FFXIVProcess?> StartGameAndCompanionApp(GameLaunchContext gameLaunchContext, bool forceNoDalamud, bool noThird, bool noPlugins)
    {
        var loginResult = gameLaunchContext.LoginResult;

        SyncGameLaunchContextAreaFromAccount(gameLaunchContext);

        if (!await EnsureFreshSessionIdAsync(gameLaunchContext).ConfigureAwait(false))
            return null;

        var stopwatch = new Stopwatch();
        stopwatch.Start();
        var dalamudSession = App.Dalamud.CreateLauncher
        (
            App.Settings.GamePath,
            new DalamudLaunchOptions
            (
                App.Settings.DalamudLoadMethod,
                (int)App.Settings.DalamudInjectionDelayMS,
                false,
                noPlugins,
                noThird
            )
        );

        var dalamudOk = false;
        EnsureDalamudCompatibility();

        if (App.Settings.DalamudEnabled && !forceNoDalamud)
        {
            if (EnsureDalamudUpdate(dalamudSession, App.Settings.GamePath, false) is not { } dalamudUpdateResult)
                return null;

            dalamudOk = dalamudUpdateResult;
        }

        var gameRunner = new GameRunner(dalamudSession, dalamudOk, App.Dalamud.Updater.Runtime);
        stopwatch.Stop();

        if (stopwatch.Elapsed > TimeSpan.FromMinutes(5))
        {
            CustomMessageBox.Show("会话已过期,请重新登录", "XIVLauncherCN (Soil)", MessageBoxButton.OK, MessageBoxImage.Exclamation, parentWindow: Window);
            return null;
        }

        // We won't do any sanity checks here anymore, since that should be handled in StartLogin
        var launched = Launcher.LaunchGame
        (
            gameRunner,
            loginResult.OAuthLogin!.SessionID,
            loginResult.OAuthLogin!.SndaID,
            gameLaunchContext.DcTravelPort,
            gameLaunchContext.Area.AreaID,
            gameLaunchContext.Area.AreaLobby,
            gameLaunchContext.Area.AreaGM,
            gameLaunchContext.Area.AreaConfigUpload,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(gameLaunchContext.Areas))),
            App.Settings.AdditionalLaunchArgs,
            App.Settings.GamePath,
            App.Settings.EncryptArgumentsV2,
            App.Settings.DPIAwareness
        );

        Troubleshooting.LogTroubleshooting();

        if (launched == null)
        {
            Log.Information("GameProcess was null...");
            IsLoggingIn = false;
            return null;
        }

        CompanionAppManager? companionAppManager = null;

        try
        {
            companionAppManager = GameLaunchService.StartCompanionApps(launched.ProcessID);
        }
        catch (Exception ex)
        {
            CustomMessageBox.Builder
                            .NewFrom(ex, "CompanionApps")
                            .WithAppendText("\n\n")
                            .WithAppendText("这可能由杀毒软件引起, 请检查日志并添加必要的排除项")
                            .WithParentWindow(Window)
                            .Show();

            IsLoggingIn = false;

            GameLaunchService.StopCompanionApps(launched.ProcessID, companionAppManager);
        }

        Log.Debug("等待游戏进程退出");

        try
        {
            if (dalamudOk)
            {
                await Launcher.RestartMonitor
                              .MonitorAsync
                              (
                                  launched,
                                  new RestartMonitor.RestartOptions(forceNoDalamud, noThird, noPlugins),
                                  options => StartGameAndCompanionApp(gameLaunchContext, options.ForceNoDalamud, options.NoThirdPlugins, options.NoPlugins),
                                  LoginCancelSource?.Token ?? CancellationToken.None
                              )
                              .ConfigureAwait(false);
            }
            else
                await Task.Run(() => launched.UnderlyingProcess.WaitForExit()).ConfigureAwait(false);
        }
        finally
        {
            GameLaunchService.StopCompanionApps(launched.ProcessID, companionAppManager);
        }

        Log.Verbose("游戏进程已退出");

        // 不在此处停止 DC Travel 服务——Dashboard 可能仍需使用。
        // 仅在切换账号或关闭启动器时才停止。

        return launched;
    }

    /// <summary>
    ///     启动游戏前实时换取一张全新的 session ticket。
    ///     ticket 单次有效且与登录时所连大区绑定，游戏外跨大区或进程重启后旧 ticket 会失效，
    ///     因此每次启动都必须用持久 TGT 现取，而非复用上次启动缓存的值。
    /// </summary>
    private async Task<bool> EnsureFreshSessionIdAsync(GameLaunchContext gameLaunchContext)
    {
        var oauthLogin = gameLaunchContext.LoginResult.OAuthLogin;
        if (oauthLogin == null)
            return true;

        if (!string.IsNullOrEmpty(oauthLogin.TGT) && !string.IsNullOrEmpty(oauthLogin.Guid))
        {
            try
            {
                var deviceProfile = oauthLogin.DeviceProfile ?? FakeMachineInfo.CreateSnapshot();
                var loginCtx      = new LoginChannelContext(deviceProfile);
                oauthLogin.SessionID = await loginCtx.GetSessionIdAsync(oauthLogin.TGT, oauthLogin.Guid).ConfigureAwait(false);
                Log.Information("[MainWindow] 已通过 TGT 实时获取 session ticket: {SessionID}", oauthLogin.SessionID[..Math.Min(8, oauthLogin.SessionID.Length)]);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] 通过 TGT 获取 session ticket 失败，登录可能已过期");
                CustomMessageBox.Show
                (
                    "登录会话已过期，请重新登录",
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    parentWindow: Window
                );
                return false;
            }
        }

        if (string.IsNullOrEmpty(oauthLogin.SessionID))
        {
            Log.Error("[MainWindow] SessionID 为空且无法通过 TGT 获取，取消登录");
            CustomMessageBox.Show
            (
                "登录异常: SessionID 为空",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                showOfficialLauncher: true,
                parentWindow: Window
            );
            return false;
        }

        return true;
    }

    private void SyncGameLaunchContextAreaFromAccount(GameLaunchContext gameLaunchContext)
    {
        var account = App.AccountManager.CurrentAccount;

        if (account?.AreaName == null)
            return;

        if (string.Equals(account.AreaName, gameLaunchContext.Area.AreaName, StringComparison.Ordinal))
            return;

        var matched = gameLaunchContext.Areas.FirstOrDefault
        (a =>
             string.Equals(a.AreaName, account.AreaName, StringComparison.Ordinal)
        );

        if (matched != null)
        {
            gameLaunchContext.Area = matched;
            Log.Information
            (
                "[DCTravel] 启动前检测到大区与账号不同步，已更新为 \"{AreaName}\" (ID={AreaID})",
                account.AreaName,
                matched.AreaID
            );
        }
        else
        {
            Log.Warning
            (
                "[DCTravel] 启动前检测到大区与账号不同步 (账号: \"{AccountArea}\", 上下文: \"{ContextArea}\")，但无法在大区列表中找到匹配项",
                account.AreaName,
                gameLaunchContext.Area.AreaName
            );
        }
    }

    private void FakeStartGame()
    {
        if (Window.Dispatcher != Dispatcher.CurrentDispatcher)
        {
            Window.Dispatcher.Invoke(FakeStartGame);
            return;
        }

        Task.Run
        (() => StartGameAndCompanionApp
         (
             new GameLaunchContext
             (
                 new LoginResult
                 {
                     OAuthLogin = new OAuthLoginResult
                     {
                         MaxExpansion  = 5,
                         Playable      = true,
                         Region        = 0,
                         SessionID     = "0",
                         TermsAccepted = true,
                         SndaID        = "114514"
                     },
                     State    = LoginState.Ok,
                     UniqueID = "0"
                 },
                 LoginPage.Area!,
                 LoginPage.LoginAreas
             ),
             false,
             false,
             false
         )
        ).ConfigureAwait(false);
    }

    #endregion

    #region 更新

    private void EnsureDalamudCompatibility()
    {
        var dalamudCompatCheck = new DalamudCompatibilityCheck();

        try
        {
            dalamudCompatCheck.EnsureCompatibility();
        }
        catch (IDalamudCompatibilityCheck.NoRedistsException ex)
        {
            Log.Error(ex, "[MainWindow] 未找到 Dalamud 所需的 Redists");

            CustomMessageBox.Show
            (
                "Dalamud 需要安装 Microsoft Visual C++ 2015-2019 Redistributable, 请前往微软官网下载并安装",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                parentWindow: Window
            );
        }
        catch (IDalamudCompatibilityCheck.ArchitectureNotSupportedException ex)
        {
            Log.Error(ex, "[MainWindow] 不受支持的本地环境架构");

            CustomMessageBox.Show
            (
                "Dalamud 仅支持 64 位 Windows\n若本机为 ARM 架构, 请检查是否已为 XIVLauncher 启用 64 位模拟器",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                parentWindow: Window
            );
        }
    }

    private bool? EnsureDalamudUpdate(DalamudSession dalamudSession, DirectoryInfo gamePath, bool appendWafStatusCodeHint)
    {
        try
        {
            App.Dalamud.RunUpdater();
            var dalamudStatus = dalamudSession.EnsureReady(gamePath);
            return dalamudStatus == DalamudSession.DalamudInstallState.Ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MainWindow] 尝试更新 Dalamud 时发生错误");

            var ensurementErrorMessage = "下载 Dalamud 相关文件异常\n请检查本地网络连接, 或关闭杀毒软件\n点击确定将继续以不启用 Dalamud 的方式启动游戏";

            if (appendWafStatusCodeHint                                                  &&
                ex is HttpRequestException { StatusCode: not null } httpRequestException &&
                (int)httpRequestException.StatusCode is 403 or 444 or 522)
                ensurementErrorMessage = $"服务器错误: {httpRequestException.StatusCode}\n{ensurementErrorMessage}";
            else
                ensurementErrorMessage = $"错误: {ex.Message}\n{ensurementErrorMessage}";

            var result = CustomMessageBox.Builder
                                         .NewFrom(ensurementErrorMessage)
                                         .WithImage(MessageBoxImage.Warning)
                                         .WithButtons(MessageBoxButton.OKCancel)
                                         .WithShowHelpLinks()
                                         .WithParentWindow(Window)
                                         .Show();
            return result == MessageBoxResult.OK ? false : null;
        }
    }

    private bool TryResolvePatchPath()
    {
        try
        {
            App.Settings.PatchPath = Paths.ResolvePatchPath(App.Settings.PatchPath, Paths.RoamingPath);
            return true;
        }
        catch (Exception ex)
        {
            CustomMessageBox.Builder
                            .NewFrom(ex, nameof(TryResolvePatchPath))
                            .WithAppendText("\n\n")
                            .WithAppendText("解析更新文件路径失败")
                            .WithParentWindow(Window)
                            .Show();
            Environment.Exit(0);

            return false;
        }
    }

    private async Task<bool> InstallGamePatchAsync()
    {
        var result = await GameClientFileTaskService.RunAsync(GameClientFileTaskKind.Update).ConfigureAwait(false);
        return result.Status == GameClientFileTaskResultStatus.Success;
    }

    #endregion

    #region 杂项

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
        refreshDalamudInfoCommand.RaiseCanExecuteChanged();

    private static string GetDalamudLoadingText(DalamudStatusSnapshot updater)
    {
        if (updater.LoadingProgress is { } progress)
            return $"Dalamud 正在加载 {progress.ToString("0.##", CultureInfo.InvariantCulture)}%";

        if (!string.IsNullOrWhiteSpace(updater.LoadingDetail))
            return $"Dalamud {updater.LoadingDetail.TrimEnd('.')}";

        return "Dalamud 正在加载";
    }

    private void HandleLoginAction(LoginPageViewModel loginPage, LoginAfterAction action)
    {
        if (IsLoggingIn)
            return;

        if (action == LoginAfterAction.Start)
            loginPage.LoginMessage = string.Empty;

        StartLogin(loginPage.LoginTypeOption.LoginType, loginPage.Username, loginPage.Password, loginPage.IsFastLogin, loginPage.IsReadWegameInfo, action);
    }

    private async Task HandleGameClientFileTask(GameClientFileTaskKind kind)
    {
        if (IsLoggingIn)
            return;

        if (!ConfirmGameClientFileTask(kind))
            return;

        IsLoggingIn = true;
        IsEnabled   = false;
        LoginPage.RefreshCommandStates();
        InjectPage.RefreshCommandStates();

        GameClientFileTaskResult result;

        try
        {
            result = await GameClientFileTaskService.RunAsync(kind).ConfigureAwait(false);
        }
        finally
        {
            Window.Dispatcher.Invoke
            (() =>
                {
                    IsLoggingIn = false;
                    IsEnabled   = true;
                    LoginPage.RefreshCommandStates();
                    InjectPage.RefreshCommandStates();
                    RefreshGameVersion();
                    Activate();
                }
            );
        }

        if (!result.ShouldLaunchGame)
            return;

        StartLogin
        (
            LoginPage.LoginTypeOption.LoginType,
            LoginPage.Username,
            LoginPage.Password,
            LoginPage.IsFastLogin,
            LoginPage.IsReadWegameInfo,
            LoginAfterAction.Start
        );
    }

    private void RefreshQrCode(LoginPageViewModel loginPage)
    {
        LoginCardAfterCompletion = LoginCardType.MainPage;
        StartLogin(LoginType.QRCode, loginPage.Username, loginPage.Password, loginPage.IsFastLogin, loginPage.IsReadWegameInfo, LoginAfterAction.Start);
    }

    private void ShowLoadingDialog(string message)
    {
        IsLoadingDialogOpen  = true;
        LoadingDialogMessage = message;
    }

    private void HideLoadingDialog() =>
        IsLoadingDialogOpen = false;

    private void HandleStartGameFromDashboard(LoginAfterAction action)
    {
        if (currentGameLaunchContext == null)
            return;

        isStartingGameFromDashboard = true;
        IsEnabled                   = false;

        _ = Task.Run
        (async () =>
            {
                try
                {
                    if (await ProcessLoginResultAsync(currentGameLaunchContext, action).ConfigureAwait(false) && App.Settings.ExitLauncherWhenGameExit)
                        Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    Window.Dispatcher.Invoke
                    (() =>
                        {
                            CustomMessageBox.Builder
                                            .NewFromUnexpectedException(ex, "Dashboard/StartGame")
                                            .WithParentWindow(Window)
                                            .Show();
                        }
                    );
                }
                finally
                {
                    Window.Dispatcher.Invoke
                    (() =>
                        {
                            IsEnabled = true;
                            Activate();
                            SwitchCard(LoginCardType.Dashboard, false);
                        }
                    );
                }
            }
        );
    }

    private void HandleSwitchAccount()
    {
        CancelLogin();
        currentGameLaunchContext = null;
        SwitchCard(LoginCardType.MainPage);
        AccountSwitcher.RefreshEntries(AccountManager.CurrentAccountID, false);
        RequestSwitchToCurrentAccount?.Invoke();

        Task.Run(() => { DcTravelRuntimeService.Stop(); });
    }

    private void HandleOpenDeviceProfile()
    {
        var account = AccountManager.CurrentAccount;
        if (account == null)
            return;

        var dialogService = new DialogService(Window);
        dialogService.ShowAccountDeviceProfileSettings(account, AccountManager);
        AccountSwitcher.RefreshEntries(AccountManager.CurrentAccountID, false);
    }

    private void HandleOpenDCTravel()
    {
        if (currentGameLaunchContext == null)
            return;

        SwitchCard(LoginCardType.DCTravel);
        _ = DCTravelPage.InitializeAsync(currentGameLaunchContext.Area.AreaName);
    }

    private void HandleSetCurrentAreaFromDCTravel(string areaName)
    {
        if (currentGameLaunchContext == null)
        {
            Log.Error("[MainWindow] currentGameLaunchContext 为空, 无法切换大区");
            return;
        }

        var matched = currentGameLaunchContext.Areas.FirstOrDefault
            (a => string.Equals(a.AreaName, areaName, StringComparison.Ordinal));

        if (matched != null)
        {
            Log.Information
            (
                "[MainWindow] DC Travel 完成，切换大区: {Old} → {New}",
                currentGameLaunchContext.Area.AreaName,
                areaName
            );

            currentGameLaunchContext.Area = matched;
            DashboardPage.AreaName        = matched.AreaName;
            DashboardPage.AreaStatus      = matched.AreaStatus;
            DashboardPage.SelectedArea    = matched;

            if (App.AccountManager.CurrentAccount != null)
            {
                App.AccountManager.CurrentAccount.AreaName = matched.AreaName;
                App.AccountManager.Save();
            }
        }
    }

    private void HandleSetAreaFromDashboard(LoginArea area)
    {
        if (currentGameLaunchContext == null)
            return;

        Log.Information
        (
            "[MainWindow] Dashboard 切换大区: {Old} → {New} (Lobby={Lobby})",
            currentGameLaunchContext.Area.AreaName,
            area.AreaName,
            area.AreaLobby
        );

        currentGameLaunchContext.Area = area;
        DashboardPage.AreaName        = area.AreaName;
        DashboardPage.AreaStatus      = area.AreaStatus;

        if (App.AccountManager.CurrentAccount != null)
        {
            App.AccountManager.CurrentAccount.AreaName = area.AreaName;
            App.AccountManager.Save();
        }
    }

    private void UpdateDashboardInfo(LoginResult loginResult)
    {
        var oauth = loginResult.OAuthLogin;
        if (oauth == null)
            return;

        DashboardPage.AccountName = oauth.InputUserID;

        RefreshGameVersion();

        if (currentGameLaunchContext != null)
        {
            var areas = currentGameLaunchContext.Areas;
            DashboardPage.Areas.Clear();
            foreach (var a in areas)
                DashboardPage.Areas.Add(a);

            DashboardPage.SelectedArea = currentGameLaunchContext.Area;
            DashboardPage.AreaName     = currentGameLaunchContext.Area.AreaName;
            DashboardPage.AreaStatus   = currentGameLaunchContext.Area.AreaStatus;
        }
    }

    private void RefreshGameVersion() =>
        DashboardPage.GameVersion = App.Settings.GamePath != null
                                        ? Repository.Ffxiv.GetVer(App.Settings.GamePath)
                                        : string.Empty;

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

    public enum LoginCardType
    {
        Logining         = 0,
        MainPage         = 1,
        ScanQRCode       = 2,
        InjectMode       = 3,
        Dashboard        = 4,
        DCTravel         = 5,
        DCTravelHistory  = 6,
        DCTravelProgress = 7,
        DCTravelReturn   = 8,
        AccountSwitcher  = 9
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
