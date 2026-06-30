using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.GamePatchV3.Models;
using XIVLauncher.Login;
using XIVLauncher.Support;
using XIVLauncher.Windows.GameClientFiles;
using XIVLauncher.Windows.ViewModel.Main.Models;
using XIVLauncher.Windows.ViewModel.Main.Providers;
using XIVLauncher.Windows.ViewModel.Main.Services;

namespace XIVLauncher.Windows.ViewModel.Main.Handlers;

internal sealed class LoginFlowHandler
(
    MainWindowViewModel       vm,
    LoginWorkflowService      loginWorkflowService,
    MainWindowDialogProvider  dialogProvider,
    DCTravelRuntimeService    dcTravelRuntimeService,
    GameClientFileTaskService gameClientFileTaskService
)
{
    private CancellationTokenSource? loginCancelSource;
    private bool                     isLoginCanceledByUser;
    private LoginCardType?           loginCardAfterCompletion;
    private bool                     isWeGameRetryingAfterThirdPartyFailure;

    /// <summary>
    ///     当前登录流程的取消令牌, 供 GameLaunchFlowHandler 在启动游戏时使用。
    /// </summary>
    public CancellationToken LoginCancellationToken =>
        loginCancelSource?.Token ?? CancellationToken.None;

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
        if (vm.Window.Dispatcher != Dispatcher.CurrentDispatcher)
        {
            vm.Window.Dispatcher.Invoke
            (() => StartLogin
             (
                 loginType,
                 username,
                 password,
                 quickLoginEnabled,
                 readWeGameInfo,
                 action
             )
            );
            return;
        }

        if (vm.IsLoggingIn)
            return;

        Log.Information("[MainWindow] 尝试开始登录");

        vm.IsLoggingIn               = true;
        vm.IsEnabled                 = false;
        isLoginCanceledByUser        = false;
        vm.LoginPage.IsQrCodeExpired = false;
        loginCancelSource?.Dispose();
        loginCancelSource = new();
        vm.LoginPage.RefreshCommandStates();
        vm.InjectPage.RefreshCommandStates();

        var currentCard = loginCardAfterCompletion ?? (LoginCardType)vm.LoginCardTransitionerIndex;
        loginCardAfterCompletion = null;
        vm.SwitchCard(loginType == LoginType.QRCode ? LoginCardType.ScanQRCode : LoginCardType.Logining, false);

        _ = Task.Run
        (
            async () =>
            {
                try
                {
                    await LoginAsync(loginType, username, password, quickLoginEnabled, readWeGameInfo, action).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    vm.Window.Dispatcher.Invoke
                    (() =>
                        {
                            CustomMessageBox.Builder
                                            .NewFromUnexpectedException(ex, "CreateLoginHandler/Task")
                                            .WithParentWindow(vm.Window)
                                            .Show();
                        }
                    );
                }
                finally
                {
                    vm.Window.Dispatcher.Invoke
                    (() =>
                        {
                            loginCancelSource?.Dispose();
                            loginCancelSource = null;

                            var nextCard = loginCardAfterCompletion ?? currentCard;
                            loginCardAfterCompletion = null;
                            vm.SwitchCard(nextCard, false);

                            vm.IsLoggingIn = false;
                            vm.IsEnabled   = true;
                            vm.LoginPage.RefreshCommandStates();
                            vm.InjectPage.RefreshCommandStates();

                            vm.ReloadHeadlines();
                            vm.Activate();
                        }
                    );
                }
            },
            LoginCancellationToken
        );
    }

    public void CancelLogin()
    {
        if (loginCancelSource != null)
        {
            Log.Information("[MainWindow] 取消登录");
            isLoginCanceledByUser = true;

            if (!loginCancelSource.IsCancellationRequested)
                loginCancelSource.Cancel();
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
        if (isLoginCanceledByUser)
            return;

        ProblemCheck.RunCheck(vm.Window);

        if (!TryResolvePatchPath())
            return;

        if (vm.LoginPage.Area == null || vm.LoginPage.Area.AreaID == "-1")
        {
            CustomMessageBox.Show
            (
                "获取服务器信息失败, 无法登录",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: vm.Window
            );

            return;
        }

        App.Settings.FastLogin = vm.LoginPage.IsFastLogin;
        inputPassword          = inputPassword == MainWindowViewModel.PRESUDO_PASSWORD ? string.Empty : inputPassword?.Trim() ?? string.Empty;

        var loginRequest = new LoginWorkflowRequest
        {
            LoginType                            = loginType,
            Username                             = username,
            Password                             = inputPassword,
            QuickLoginEnabled                    = quickLoginEnabled,
            ReadWeGameInfo                       = readWeGameInfo,
            ForceWeGameTokenRecapture            = loginType == LoginType.WeGame && readWeGameInfo,
            Action                               = action,
            CurrentArea                          = vm.LoginPage.Area,
            LoginAreas                           = vm.LoginPage.LoginAreas,
            LoginCancellationTokenSource         = loginCancelSource ?? new CancellationTokenSource(),
            LoginSessionRefreshSink              = dcTravelRuntimeService,
            Interaction                          = new MainWindowLoginInteraction(vm.Window, vm.LoginPage, dialogProvider),
            RequireDeviceProfileSetupForNewLogin = App.Settings.RequireDeviceProfileSetupForNewLogin
        };

        LoginWorkflowResult? workflowResult = null;

        try
        {
            workflowResult = await loginWorkflowService.ExecuteAsync(loginRequest).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await HandleLoginWorkflowExceptionAsync
            (
                ex,
                loginType,
                username,
                workflowResult?.UsedSavedWeGameToken == true,
                action
            ).ConfigureAwait(false);
            return;
        }

        if (workflowResult == null)
            return;

        if (workflowResult.IsAccountPersisted)
        {
            vm.Window.Dispatcher.Invoke
            (() =>
                {
                    vm.AccountSwitcher.RefreshEntries(vm.AccountManager.CurrentAccountID, false);

                    if (workflowResult.IsNewAccount)
                        vm.ShowSnackbar($"新账号已保存: {vm.AccountManager.CurrentAccount?.DisplayName}");
                }
            );
        }

        var gameLaunchContext = workflowResult.GameLaunchContext;
        vm.CurrentGameLaunchContext = gameLaunchContext;
        var oAuthLogin = gameLaunchContext.LoginResult.OAuthLogin;

        Log.Information
        (
            "[MainWindow] 登录结果: {State} {Playable}",
            gameLaunchContext.LoginResult.State,
            oAuthLogin?.Playable
        );

        if (workflowResult.RefreshGameSessionIdByQuickLoginFunc != null)
            dcTravelRuntimeService.ConfigureQuickLoginRefresh(workflowResult.RefreshGameSessionIdByQuickLoginFunc);

        gameLaunchContext.DcTravelPort = gameLaunchContext.LoginResult.State == LoginState.Ok
                                             ? await dcTravelRuntimeService.StartAsync(true, false).ConfigureAwait(false)
                                             : 0;

        if (await ProcessLoginResultAsync(gameLaunchContext, action).ConfigureAwait(false))
            vm.Activate();
    }

    private async Task HandleLoginWorkflowExceptionAsync
    (
        Exception        ex,
        LoginType        loginType,
        string           username,
        bool             usedSavedWeGameToken,
        LoginAfterAction action
    )
    {
        Log.Error(ex, "[MainWindow] 尝试登录至游戏失败");

        if (ex is OperationCanceledException && (isLoginCanceledByUser || loginCancelSource?.IsCancellationRequested == true))
        {
            Log.Information("[MainWindow] 用户取消了登录操作, 正常返回");
            return;
        }

        await ClearInvalidSavedWeGameTokenAsync
        (
            ex,
            loginType,
            username,
            usedSavedWeGameToken
        ).ConfigureAwait(false);

        var msgbox = new CustomMessageBox.Builder()
                     .WithCaption("登录异常")
                     .WithImage(MessageBoxImage.Error)
                     .WithShowHelpLinks()
                     .WithShowDiscordLink()
                     .WithParentWindow(vm.Window);

        if (ex is LoginException sdoLoginEx)
        {
            if (isLoginCanceledByUser || loginCancelSource?.IsCancellationRequested == true)
            {
                Log.Information("[MainWindow] 手动取消登录");
                return;
            }

            switch (loginType)
            {
                case LoginType.WeGame when sdoLoginEx.ErrorCode == (int)LoginExceptionCode.ThirdPartyVerificationFailed:
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

                    break;
                }
                case LoginType.QRCode when sdoLoginEx.Message == "二维码不存在或已过期，请重试":
                    Log.Information("[MainWindow] 二维码已过期，等待手动刷新");
                    vm.Window.Dispatcher.Invoke(() => vm.LoginPage.IsQrCodeExpired = true);
                    loginCardAfterCompletion = LoginCardType.ScanQRCode;
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
                var account = vm.AccountManager.Accounts.FirstOrDefault(x => x.UserName == username);

                if (account != null)
                {
                    account.SdoQuickLoginSecret = string.Empty;
                    vm.AccountManager.Save(account);
                }
            }

            new CustomMessageBox.Builder()
                .WithCaption("登录异常")
                .WithImage(MessageBoxImage.Question)
                .WithParentWindow(vm.Window)
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
                if (Repository.Ffxiv.IsBaseVer(App.Settings.GamePath))
                {
                    vm.IsLoggingIn = false;
                    _              = HandleGameClientFileTask(GameClientFileTaskKind.FreshInstall);
                    return;
                }

                msgbox.WithText("无法确认当前游戏版本, 请先运行游戏文件修复后重试");
                break;
            }

            case InvalidDataException invalidDataException when invalidDataException.Message.Contains("当前游戏数据版本", StringComparison.Ordinal):
            {
                if (Repository.Ffxiv.IsBaseVer(App.Settings.GamePath))
                {
                    vm.IsLoggingIn = false;
                    _              = HandleGameClientFileTask(GameClientFileTaskKind.FreshInstall);
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
                    vm.Window.Dispatcher.Invoke(() => vm.LoginPage.IsQrCodeExpired = true);
                    loginCardAfterCompletion = LoginCardType.ScanQRCode;
                    return;
                }

                vm.LoginPage.LoginMessage = string.Empty;

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

        var account = vm.AccountManager.Accounts.FirstOrDefault
        (item => item.AccountType == XIVAccountType.WeGame && string.Equals(item.WeGameLoginAccount, username, StringComparison.Ordinal)
        );

        if (account?.WeGameQuickLoginSecret == null)
            return;

        await Task.Delay(1, LoginCancellationToken);

        account.WeGameQuickLoginSecret = null;
        vm.AccountManager.Save(account);
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

        return CustomMessageBox.Show(message, title, MessageBoxButton.YesNo, parentWindow: vm.Window) != MessageBoxResult.No;
    }

    /// <summary>
    ///     处理登录结果: 检查状态, 若 Ok 则进入 Dashboard。
    ///     不在此处启动游戏——启动游戏由 DashboardFlowHandler 通过 GameLaunchFlowHandler 完成。
    /// </summary>
    private async Task<bool> ProcessLoginResultAsync
    (
        GameLaunchContext gameLaunchContext,
        LoginAfterAction  action
    )
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
                    parentWindow: vm.Window
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
                    parentWindow: vm.Window
                );

                return false;

            case LoginState.NeedsPatchBoot:
                CustomMessageBox.Show
                (
                    "部分游戏文件损坏, 当前无法更新与启动游戏, 请重新安装游戏",
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    parentWindow: vm.Window
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
                parentWindow: vm.Window
            );
            return false;
        }

        if (CustomMessageBox.AssertOrShowError
                (loginResult.State == LoginState.Ok, "ProcessLoginResultAsync: loginResult.State should have been Launcher.LoginState.Ok", parentWindow: vm.Window))
            return false;

        // 防止 StartLogin 的 finally 块切回 MainPage
        loginCardAfterCompletion = LoginCardType.Dashboard;
        vm.Window.Dispatcher.Invoke
        (() =>
            {
                vm.DashboardFlow.UpdateDashboardInfo(loginResult);
                vm.SwitchCard(LoginCardType.Dashboard, false);
            }
        );
        return true;
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
                            .WithParentWindow(vm.Window)
                            .Show();
            Environment.Exit(0);

            return false;
        }
    }

    #region LoginPageViewModel 回调

    public void HandleLoginAction(LoginPageViewModel loginPage, LoginAfterAction action)
    {
        if (vm.IsLoggingIn)
            return;

        if (action == LoginAfterAction.Start)
            loginPage.LoginMessage = string.Empty;

        StartLogin(loginPage.LoginTypeOption.LoginType, loginPage.Username, loginPage.Password, loginPage.IsFastLogin, loginPage.IsReadWegameInfo, action);
    }

    public async Task HandleGameClientFileTask(GameClientFileTaskKind kind)
    {
        if (vm.IsLoggingIn)
            return;

        if (!ConfirmGameClientFileTask(kind))
            return;

        vm.IsLoggingIn = true;
        vm.IsEnabled   = false;
        vm.LoginPage.RefreshCommandStates();
        vm.InjectPage.RefreshCommandStates();

        GameClientFileTaskResult result;

        try
        {
            result = await gameClientFileTaskService.RunAsync(kind).ConfigureAwait(false);
        }
        finally
        {
            vm.Window.Dispatcher.Invoke
            (() =>
                {
                    vm.IsLoggingIn = false;
                    vm.IsEnabled   = true;
                    vm.LoginPage.RefreshCommandStates();
                    vm.InjectPage.RefreshCommandStates();
                    vm.DashboardFlow.RefreshGameVersion();
                    vm.Activate();
                }
            );
        }

        if (!result.ShouldLaunchGame)
            return;

        StartLogin
        (
            vm.LoginPage.LoginTypeOption.LoginType,
            vm.LoginPage.Username,
            vm.LoginPage.Password,
            vm.LoginPage.IsFastLogin,
            vm.LoginPage.IsReadWegameInfo,
            LoginAfterAction.Start
        );
    }

    public void RefreshQrCode(LoginPageViewModel loginPage)
    {
        loginCardAfterCompletion = LoginCardType.MainPage;
        StartLogin
        (
            LoginType.QRCode,
            loginPage.Username,
            loginPage.Password,
            loginPage.IsFastLogin,
            loginPage.IsReadWegameInfo,
            LoginAfterAction.Start
        );
    }

    #endregion
}
