using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Castle.Core.Internal;
using FfxivArgLauncher;
using MaterialDesignThemes.Wpf;
using Serilog;
using XIVLauncher.Accounts;
using XIVLauncher.Common;
using XIVLauncher.Common.Addon;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.DCTravel;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Game.Patch;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;
using XIVLauncher.Common.Windows;
using XIVLauncher.Game;
using XIVLauncher.PlatformAbstractions;
using XIVLauncher.Support;
using XIVLauncher.Xaml;
using Constants = XIVLauncher.Common.Constants;
using DCTravelClient = XIVLauncher.Common.Game.DCTravel.DCTravelClient;

namespace XIVLauncher.Windows.ViewModel;

public class MainWindowViewModel : INotifyPropertyChanged
{
    public const string PRESUDO_PASSWORD = "********假的密码********";

    public Launcher          Launcher         { get; private set; }
    public AccountManager    AccountManager   { get; private set; } = App.AccountManager;
    public DCTravelListener? DCTravelListener { get; private set; }
    public Window            Window           { get; private set; }

    public Action Activate        { get; set; } = null!;
    public Action Hide            { get; set; } = null!;
    public Action ReloadHeadlines { get; set; } = null!;

    public  bool                     IsLoggingIn                { get; set; }
    public  string                   Password                   { get; set; } = null!;
    private CancellationTokenSource? LoginCancelSource          { get; set; }
    private CancellationTokenSource? ProcessRefreshCancelSource { get; set; }
    private Task?                    ProcessRefreshTask         { get; set; }
    private bool                     IsInjecting                { get; set; }

    public MainWindowViewModel(Window window)
    {
        Window = window;

        StartLoginCommand       = new SyncCommand(CreateLoginHandler(LoginAfterAction.Start),               () => !IsLoggingIn);
        LoginNoStartCommand     = new SyncCommand(CreateLoginHandler(LoginAfterAction.UpdateOnly),          () => !IsLoggingIn);
        LoginNoDalamudCommand   = new SyncCommand(CreateLoginHandler(LoginAfterAction.StartWithoutDalamud), () => !IsLoggingIn);
        LoginNoPluginsCommand   = new SyncCommand(CreateLoginHandler(LoginAfterAction.StartWithoutPlugins), () => !IsLoggingIn);
        LoginNoThirdCommand     = new SyncCommand(CreateLoginHandler(LoginAfterAction.StartWithoutThird),   () => !IsLoggingIn);
        LoginRepairCommand      = new SyncCommand(CreateLoginHandler(LoginAfterAction.Repair),              () => !IsLoggingIn);
        LoginForceQRCommand     = new SyncCommand(CreateLoginHandler(LoginAfterAction.ForceQR),             () => !IsLoggingIn);
        InjectModeSwitchCommand = new SyncCommand(_ => { SwitchMode(); },                                   () => !IsLoggingIn);
        InjectGameCommand       = new SyncCommand(_ => { StartInject(); },                                   () => !IsLoggingIn && SelectedProcess != null);
        FakeStartCommand        = new SyncCommand(_ => { FakeStartGame(); },                                () => !IsLoggingIn);
        LoginCancelCommand      = new SyncCommand(CreateLoginHandler(LoginAfterAction.CancelLogin));

        Launcher = new();

        var worldStatusBrushOk = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xf3));
        WorldStatusIconColor = worldStatusBrushOk;

        WorldStatusIconColor   = new SolidColorBrush(Color.FromRgb(38, 38, 38));
        ModeSwitchIcon         = PackIconKind.Injection;
        FFXIVProcesses.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAvailableProcesses));
            OnPropertyChanged(nameof(ProcessSelectionHint));
            CommandManager.InvalidateRequerySuggested();
        };
    }

    #region 界面控制

    public void SwitchCard(LoginCardType i) =>
        Window.Dispatcher.Invoke
        (() =>
            {
                CancelLogin();
                LoginCardTransitionerIndex = (int)i;
                ModeSwitchIcon             = i == LoginCardType.InjectMode ? PackIconKind.Login : PackIconKind.Injection;
                if (LoginCardTransitionerIndex == (int)LoginCardType.InjectMode)
                    StartRefreshFFXIVProcess();
                else
                    StopRefreshFFXIVProcess(true);
            }
        );

    public void SwitchMode() =>
        SwitchCard(LoginCardTransitionerIndex == (int)LoginCardType.InjectMode ? LoginCardType.MainPage : LoginCardType.InjectMode);

    #endregion

    #region 登录

    public void StartLogin
    (
        LoginType        loginType,
        string           username,
        string           password,
        bool             doingAutoLogin,
        bool             readWeGameInfo,
        LoginAfterAction action
    )
    {
        if (Window.Dispatcher != Dispatcher.CurrentDispatcher)
        {
            Window.Dispatcher.Invoke(() => StartLogin(loginType, username, password, doingAutoLogin, readWeGameInfo, action));
            return;
        }
        
        if (IsLoggingIn)
            return;
        
        Log.Information("[MainWindow] 尝试开始登录");

        IsLoggingIn       = true;
        IsEnabled         = false;
        LoginCancelSource = new();
        CommandManager.InvalidateRequerySuggested();

        var currentCard = (LoginCardType)LoginCardTransitionerIndex;
        SwitchCard(loginType == LoginType.QRCode ? LoginCardType.ScanQRCode : LoginCardType.Logining);

        Task.Run
        (() =>
            {
                try
                {
                    LoginAsync(loginType, username, password, doingAutoLogin, readWeGameInfo, action).Wait();
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Builder
                                    .NewFromUnexpectedException(ex, "CreateLoginHandler/Task")
                                    .WithParentWindow(Window)
                                    .Show();
                }
                finally
                {
                    SwitchCard(currentCard);

                    IsLoggingIn = false;
                    IsEnabled   = true;
                    CommandManager.InvalidateRequerySuggested();

                    ReloadHeadlines();
                    Activate();
                }
            }
        );
    }

    public void CancelLogin()
    {
        if (LoginCancelSource != null)
        {
            Log.Information("[MainWindow] 取消登录");

            if (!LoginCancelSource.IsCancellationRequested)
                LoginCancelSource.Cancel();
            
            LoginCancelSource.Dispose();
            LoginCancelSource = null;
        }
    }
    
    private async Task LoginAsync
    (
        LoginType        loginType,
        string           username,
        string?          inputPassword,
        bool             doingAutoLogin,
        bool             readWeGameInfo,
        LoginAfterAction action
    )
    {
        ProblemCheck.RunCheck(Window);
        
        if (!TryResolvePatchPath())
            return;

        if (Area == null || Area.AreaID == "-1")
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

        if (!doingAutoLogin) 
            App.Settings.AutologinEnabled = IsAutoLogin;
        App.Settings.FastLogin = IsFastLogin;
        
        var finalLoginType = loginType;
        var secret         = string.Empty;
        var accountType  = loginType.ToAccountType();
        
        try
        {
            inputPassword = inputPassword == PRESUDO_PASSWORD ? string.Empty : inputPassword?.Trim() ?? string.Empty;
            
            var savedAccount = AccountManager.Accounts.FirstOrDefault(x => x.UserName == username && x.AccountType == accountType);
            switch (loginType)
            {
                case LoginType.Static:
                    if (!inputPassword.IsNullOrEmpty())
                        secret = inputPassword;
                    else if (savedAccount?.Password is { Length: > 0 } savedPassword)
                        secret = await AccountManager.Decrypt(savedPassword);

                    ArgumentException.ThrowIfNullOrEmpty(username, "静态登录用户名");
                    ArgumentException.ThrowIfNullOrEmpty(secret,   "静态登录密码");
                    finalLoginType = LoginType.Static;
                    break;

                case LoginType.WeGameSID:
                    doingAutoLogin = true;
                    if (!readWeGameInfo && savedAccount?.TestSID is { Length: > 0 } testSid)
                        secret = await AccountManager.Decrypt(testSid);

                    readWeGameInfo = username.IsNullOrEmpty() || secret.IsNullOrEmpty();
                    if (readWeGameInfo)
                    {
                        var loginData = await ReadWeGameAccountInfoAsync();
                        if (loginData == null)
                            return;

                        if (loginData.SndaID.IsNullOrEmpty() || loginData.SessionId.IsNullOrEmpty())
                            throw new Exception("获取WeGame登录信息失败");
                        username = loginData.SndaID;
                        secret   = loginData.SessionId;
                        var areaId = loginData.Args.Where(x => x.Contains("AreaID=")).Select(x => x.Split('=')[1]).First();
                        Area = LoginAreas.FirstOrDefault(x => x.AreaID == areaId) ?? Area;
                    }

                    finalLoginType = LoginType.WeGameSID;
                    break;

                case LoginType.WeGameToken:
                    if (inputPassword.IsNullOrEmpty() && savedAccount?.AutoLoginSessionKey is { Length: > 0 } autoLoginSessionKey)
                    {
                        secret = await AccountManager.CredProvider.Decrypt(autoLoginSessionKey);
                        finalLoginType = LoginType.AutoLoginSession;
                    }

                    if (secret.IsNullOrEmpty())
                    {
                        secret         = inputPassword;
                        finalLoginType = LoginType.WeGameToken;
                    }

                    ArgumentException.ThrowIfNullOrEmpty(secret, "WeGame 自动登录密钥");
                    break;

                case LoginType.Slide:
                    if (doingAutoLogin && savedAccount?.AutoLoginSessionKey is { Length: > 0 } slideAutoLoginSessionKey)
                    {
                        secret = await AccountManager.Decrypt(slideAutoLoginSessionKey);
                        finalLoginType = LoginType.AutoLoginSession;
                    }

                    if (secret.IsNullOrEmpty())
                        finalLoginType = LoginType.Slide;
                    ArgumentException.ThrowIfNullOrEmpty(username, "一键登录用户名");
                    break;

                case LoginType.QRCode:
                    break;
            }
        }
        catch (Exception ex)
        {
            if (ex is ArgumentException argEx)
            {
                Log.Error(ex, "[MainWindow] 加密文本失败");
                CustomMessageBox.Show
                (
                    $"错误: {argEx.ParamName} 不能为空",
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                return;
            }

            throw;
        }

        var dcTraveler = new DCTravelClient(string.Empty)
        {
            SetSdoAreaFunc = name =>
            {
                App.AccountManager.CurrentAccount.AreaName = name;
                App.AccountManager.Save();
            }
        };

        var loginResult = await TryLoginToGameAsync(finalLoginType, loginType, username, secret, doingAutoLogin, dcTraveler, action).ConfigureAwait(false);
        if (loginResult == null)
            return;

        var oAuthLogin = loginResult.OAuthLogin;

        loginResult.Area  = Area;
        loginResult.Areas = LoginAreas;

        if (loginResult.State == LoginState.NeedsPatchGame && action != LoginAfterAction.Repair)
            action = LoginAfterAction.UpdateOnly;

        if (action != LoginAfterAction.UpdateOnly)
        {
            if (loginResult.State == LoginState.Ok)
            {
                if (App.Settings.InGameAddonEnabled && loginType != LoginType.WeGameSID)
                {
                    Log.Information("[DCTravelListener] 正在开启监听用端口");
                    await dcTraveler.GetValidCookie();
                    _ = dcTraveler.KeepCookieAlive();

                    loginResult.DCTravelPort = APIHelper.GetAvailablePort();
                    DCTravelListener         = new(dcTraveler, loginResult.DCTravelPort, false);
                    Log.Information("[DCTravelListener] 打开监听端口: {LoginResultDCTravelPort}", loginResult.DCTravelPort);
                    _ = DCTravelListener.StartAsync();
                }

                var accountToSave = new XIVAccount
                {
                    AutoLogin    = loginType == LoginType.WeGameSID || doingAutoLogin,
                    LoginAccount = oAuthLogin?.InputUserID!,
                    SndaId       = oAuthLogin?.SndaID!,
                    AccountType  = loginType.ToAccountType(),
                    AreaName     = Area.AreaName
                };

                if (doingAutoLogin && accountToSave.AccountType != XIVAccountType.WeGameSID)
                {
                    if (!string.IsNullOrEmpty(oAuthLogin?.AutoLoginSessionKey))
                        accountToSave.AutoLoginSessionKey = await AccountManager.Encrypt(oAuthLogin.AutoLoginSessionKey);

                    if (DCTravelListener != null && !string.IsNullOrEmpty(oAuthLogin?.AutoLoginSessionKey))
                    {
                        DCTravelListener.DCTravelClient.RefreshGameSessionIDByAutoLoginFunc = async () =>
                        {
                            var newLoginResult = await Launcher.LoginClient.LoginBySessionKey(username, oAuthLogin.AutoLoginSessionKey, DCTravelListener.DCTravelClient).ConfigureAwait
                                                     (false);
                            return newLoginResult.OAuthLogin?.SessionID ?? string.Empty;
                        };
                    }

                    if (finalLoginType == LoginType.Static)
                        accountToSave.Password = await AccountManager.Encrypt(secret);
                }

                if (accountToSave.AccountType == XIVAccountType.WeGameSID)
                    accountToSave.TestSID = await AccountManager.Encrypt(secret);

                accountToSave.GenerateID();
                AccountManager.AddAccount(accountToSave);
                AccountManager.CurrentAccount = accountToSave;
                AccountManager.Save();
            }
        }

        Log.Information
        (
            "[MainWindow] 登录结果: {State} {NumPatches} {Playable}",
            loginResult.State,
            loginResult.PendingPatches?.Length,
            oAuthLogin?.Playable
        );

        await AccountManager.CredProvider.ClearCache();
        secret = string.Empty;

        if (await ProcessLoginResultAsync(loginResult, action).ConfigureAwait(false))
        {
            if (App.Settings.ExitLauncherAfterGameExit ?? true)
                Environment.Exit(0);
        }
    }
    
    private async Task<LoginData?> ReadWeGameAccountInfoAsync()
    {
        try
        {
            Process.Start
            (
                new ProcessStartInfo
                {
                    FileName        = "wegame://StartFor=2000340",
                    UseShellExecute = true
                }
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MainWindow] 无法启动 WeGame");
        }

        var pidList   = FFXIVProcess.GetGameProcessIDs().ToArray();
        var argReader = new RemoteArgReader();

        try
        {
            await argReader.Start();
        }
        catch (Win32Exception ex)
        {
            throw new Win32Exception($"错误: {ex.Message}\n请尝试手动运行 {Path.Combine(AppContext.BaseDirectory, "XIVLauncher.ArgReader.exe")} 后重启 XIVLauncherCN");
        }

        while (true)
        {
            if (LoginCancelSource?.IsCancellationRequested ?? false)
            {
                argReader.Stop(false);
                return null;
            }

            await Task.Delay(1000);
            var newPidList = FFXIVProcess.GetGameProcessIDs().Except(pidList).ToArray();
            LoginMessage = "请使用 WeGame 登录需要读取的 FFXIV 账号信息并启动游戏";
#if DEBUG
            newPidList = FFXIVProcess.GetGameProcessIDs().ToArray();
#endif
            if (newPidList.Length == 0)
                continue;
            var pid = newPidList.First();
            await argReader.OpenProcess(pid);
            var data = await argReader.ReadArgs();
#if DEBUG
            LoginMessage = "读取成功";
#endif
            argReader.Stop(true);
            return data;
        }
    }
    
    private async Task<LoginResult?> TryLoginToGameAsync
    (
        LoginType        type,
        LoginType        fallbackLoginType,
        string           username,
        string           secret,
        bool             autoLogin,
        DCTravelClient   dcTravelClient,
        LoginAfterAction action
    )
    {
        if (Area == null || Area.AreaID == "-1")
        {
            CustomMessageBox.Show
            (
                "获取服务器信息失败, 无法登录",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: Window
            );
            
            return null;
        }
        
        try
        {
            LoginCancelSource ??= new CancellationTokenSource();
            var loginCts = LoginCancelSource;
            var gamePath = App.Settings.GamePath;
            return await Launcher.LoginClient.LoginWithPatchCheck
                   (
                       _ => Launcher.UpdateClient.Check(Area, gamePath, action == LoginAfterAction.Repair),
                       action == LoginAfterAction.UpdateOnly,
                       type,
                       fallbackLoginType,
                       requestLoginType => LoginRequest.Create
                       (
                           username,
                           secret,
                           autoLogin,
                           dcTravelClient,
                           loginCts,
                           qrBytes =>
                           {
                               if (requestLoginType != LoginType.QRCode)
                                   return;

                               QRCodeBitmapImage = qrBytes.ToBitmapImage();
                           },
                           code =>
                           {
                               if (requestLoginType != LoginType.Slide)
                                   return;

                               Log.Information($"叨鱼确认码:{code}");
                               LoginMessage = $"确认码: {code}";
                           }
                       ),
                       loginCts.Token
                   ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "StartGame failed");

            var msgbox = new CustomMessageBox.Builder()
                         .WithCaption("登录问题")
                         .WithImage(MessageBoxImage.Error)
                         .WithShowHelpLinks()
                         .WithShowDiscordLink()
                         .WithParentWindow(Window);

            if (ex is LoginException sdoLoginEx)
            {
                if (LoginCancelSource?.IsCancellationRequested ?? false)
                {
                    Log.Information("手动取消登录");
                    return null;
                }

                if (sdoLoginEx.RemoveAutoLoginSessionKey)
                {
                    Log.Information("快速登录失败,清除 SessionKey: {Username}", username);
                    var account = AccountManager.Accounts.FirstOrDefault(x => x.UserName == username);

                    if (account != null)
                    {
                        account.AutoLoginSessionKey = string.Empty;
                        AccountManager.Save(account);
                    }
                }

                msgbox = new CustomMessageBox.Builder()
                         .WithCaption("登录异常")
                         .WithImage(MessageBoxImage.Question)
                         .WithParentWindow(Window)
                         .WithText($"{sdoLoginEx.Message}\n(错误码: {sdoLoginEx.ErrorCode})");
                msgbox.Show();
                return null;
            }

            var disableAutoLogin = false;

            if (ex is IOException)
            {
                msgbox
                    .WithText("无法找到游戏数据文件")
                    .WithAppendText("\n\n")
                    .WithAppendText("游戏路径可能设置无效, 例如位于断开连接的硬盘或网络存储上, 请在设置中检查游戏路径");
            }
            else if (ex is InvalidVersionFilesException)
            {
                msgbox.WithTextFormatted
                (
                    "无法从游戏文件中读取版本信息\n\n需要重新安装或修复游戏文件, 右键点击 XIVLauncher 中的登录按钮并选择\"修复游戏\"",
                    ex.Message
                );
            }
            else if (ex is OAuthLoginException oauthLoginException)
            {
                disableAutoLogin = true;
                LoginMessage     = "";
                QRDialog.CloseQRWindow(Window);

                if (string.IsNullOrWhiteSpace(oauthLoginException.OauthErrorMessage))
                {
                    msgbox.WithText("无法登录到 SE 账户\n请检查用户名和密码");
                }
                else
                {
                    msgbox.WithText
                    (
                        oauthLoginException.OauthErrorMessage
                                           .Replace("\\r\\n", "\n")
                                           .Replace("\r\n",   "\n")
                    );
                }
            }

            else if (ex is HttpRequestException or TaskCanceledException or WebException)
            {
                msgbox.WithText("XIVLauncher 无法连接到游戏服务器\n\n这可能是暂时性问题或网络连接问题, 请稍后重试");
            }
            else if (ex is InvalidResponseException iex)
            {
                Log.Error("Invalid response from server! Context: {Message}\n{Document}", ex.Message, iex.Document);

                msgbox.WithText("服务器返回无效响应, 这通常发生在服务器中断或负载过高时\n请等待一分钟后重试, 或使用官方启动器\n\n可以在 Lodestone 上了解更多中断信息");
            }
            // Actual unexpected error; show error details
            else
            {
                disableAutoLogin = true;
                msgbox.WithShowNewGitHubIssue()
                      .WithAppendDescription(ex.ToString())
                      .WithAppendSettingsDescription("Login")
                      .WithAppendText("\n\n")
                      .WithAppendText("请检查登录信息或稍后重试");
            }

            if (disableAutoLogin && App.Settings.AutologinEnabled)
            {
                msgbox.WithAppendText("\n\n自动登录已被禁用");
                App.Settings.AutologinEnabled = false;
            }

            msgbox.Show();
            return null;
        }
    }

    private async Task<bool> ProcessLoginResultAsync(LoginResult loginResult, LoginAfterAction action)
    {
        if (loginResult.State == LoginState.NoService)
        {
            CustomMessageBox.Show
            (
                "此账户没有游戏权限, 请确保账户已激活且订阅未过期\n\n如果启用了自动登录, 请在启动时按住 Shift 键以访问设置",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                false,
                false,
                parentWindow: Window
            );

            return false;
        }

        if (loginResult.State == LoginState.NoTerms)
        {
            CustomMessageBox.Show
            (
                "请在官方启动器中接受使用条款",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                showOfficialLauncher: true,
                parentWindow: Window
            );

            return false;
        }

        if (loginResult.State == LoginState.NeedsPatchBoot)
        {
            CustomMessageBox.Show
            (
                "部分游戏文件已损坏或被第三方篡改, 无法进行更新和启动\n请重新安装游戏以修复此问题",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: Window
            );

            return false;
        }

        if (action == LoginAfterAction.Repair)
        {
            try
            {
                if (loginResult.State == LoginState.NeedsPatchGame)
                {
                    if (!await RepairGame(loginResult).ConfigureAwait(false))
                        return false;

                    loginResult.State = LoginState.Ok;
                }
                else
                {
                    CustomMessageBox.Show
                    (
                        "服务器返回错误响应, 无法进行修复",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        parentWindow: Window
                    );

                    return false;
                }
            }
            catch (Exception ex)
            {
                /*
                 * We should never reach here.
                 * If server responds badly, then it should not even have reached this point, as error cases should have been handled before.
                 * If RepairGame was unsuccessful, then it should have handled all of its possible errors, instead of propagating it upwards.
                 */
                CustomMessageBox.Builder.NewFrom(ex, "ProcessLoginResultAsync/Repair").WithParentWindow(Window).Show();

                return false;
            }
        }

        if (loginResult.State == LoginState.NeedsPatchGame)
        {
            if (App.Settings.AskBeforePatchInstall ?? true)
            {
                var selfPatchAsk = CustomMessageBox.Show
                (
                    "发现新的补丁需要安装才能游玩\n是否让 XIVLauncher 安装?",
                    "Out of date",
                    MessageBoxButton.YesNo,
                    parentWindow: Window
                );

                if (selfPatchAsk == MessageBoxResult.No)
                    return false;
            }

            if (!await InstallGamePatch(loginResult).ConfigureAwait(false))
            {
                Log.Error("patchSuccess != true");
                return false;
            }

            loginResult.State = LoginState.Ok;
        }

        if (action == LoginAfterAction.UpdateOnly)
        {
            CustomMessageBox.Show
            (
                "更新检查已完成, 所有待安装的更新均已安装",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Information,
                false,
                false,
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

        if (CustomMessageBox.AssertOrShowError(loginResult.State == LoginState.Ok, "ProcessLoginResultAsync: loginResult.State should have been Launcher.LoginState.Ok", parentWindow: Window))
            return false;

        Hide();

        while (true)
        {
            List<Exception> exceptions = new();

            try
            {
                var oauthLogin = loginResult.OAuthLogin;

                if (oauthLogin == null || oauthLogin.SessionID.IsNullOrEmpty() || oauthLogin.SndaID.IsNullOrEmpty())
                {
                    Log.Error("SID或SNDAID为空，取消登录");
                    CustomMessageBox.Show("SID或SNDAID为空", "Error", MessageBoxButton.OK, MessageBoxImage.Error, showOfficialLauncher: true, parentWindow: Window);
                    return false;
                }

                using var process = await StartGameAndAddon
                                    (
                                        loginResult,
                                        action == LoginAfterAction.StartWithoutDalamud,
                                        action == LoginAfterAction.StartWithoutThird,
                                        action == LoginAfterAction.StartWithoutPlugins
                                    ).ConfigureAwait(false);

                if (process == null)
                    return false;

                // 正常退出 / 重启
                if (process.ExitCode is not (0 or 0x12345678) && (App.Settings.TreatNonZeroExitCodeAsFailure ?? false))
                {
                    switch (new CustomMessageBox.Builder()
                            .WithTextFormatted
                            (
                                "游戏似乎因致命错误而退出, 是否重新启动?\n\n退出代码: 0x{0:X8}",
                                (uint)process.ExitCode
                            )
                            .WithImage(MessageBoxImage.Exclamation)
                            .WithShowHelpLinks()
                            .WithShowDiscordLink()
                            .WithShowNewGitHubIssue()
                            .WithButtons(MessageBoxButton.YesNoCancel)
                            .WithDefaultResult(MessageBoxResult.Yes)
                            .WithCancelResult(MessageBoxResult.No)
                            .WithYesButtonText("重启 (_R)")
                            .WithNoButtonText("关闭 (_C)")
                            .WithCancelButtonText("不再询问 (_D)")
                            .WithParentWindow(Window)
                            .Show())
                    {
                        case MessageBoxResult.Yes:
                            continue;

                        case MessageBoxResult.No:
                            return true;

                        case MessageBoxResult.Cancel:
                            App.Settings.TreatNonZeroExitCodeAsFailure = false;
                            return true;
                    }
                }

                return true;
            }
            catch (AggregateException ex)
            {
                Log.Error(ex, "StartGameAndError resulted in one or more exceptions.");

                var innerException = ex.Flatten().InnerException;
                if (innerException != null)
                    exceptions.Add(innerException);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "StartGameAndError resulted in an exception.");

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
                          .WithYesButtonText("重试 (_T)")
                          .WithNoButtonText("关闭 (_C)")
                          .WithParentWindow(Window);

            //NOTE(goat): This HAS to handle all possible exceptions from StartGameAndAddon!!!!!
            List<string>  summaries    = new();
            List<string>  actionables  = new();
            List<string?> descriptions = new();

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
                            summaries.Add("默认情况下无法启动两个以上的游戏实例");
                            actionables.Add($"请检查是否存在未正确关闭的游戏实例 (检测到: {count})");
                            descriptions.Add(null);

                            builder.WithButtons(MessageBoxButton.YesNoCancel)
                                   .WithDefaultResult(MessageBoxResult.Yes)
                                   .WithCancelButtonText("终止后再试 (_K)");
                        }
                        else
                        {
                            summaries.Add("XIVLauncher 无法正确启动游戏");
                            descriptions.Add(null);

                            var actionableText = "这可能是暂时性问题, 请尝试重启电脑\n游戏安装可能无效 - 可以右键点击登录按钮并选择\"修复游戏\"来修复安装";
                            actionableText += "\n此问题也可能由杀毒软件误将 XIVLauncher 标记为恶意软件引起, 可能需要在设置中添加排除项 - 请查看常见问题获取更多信息";

                            actionables.Add(actionableText);
                        }

                        builder.WithShowNewGitHubIssue(false);

                        break;

                    case BinaryNotPresentException:
                        summaries.Add("找不到游戏可执行文件");
                        actionables.Add("这可能由杀毒软件引起, 可能需要重新安装游戏");
                        descriptions.Add(null);
                        break;

                    case IOException:
                        summaries.Add("无法找到游戏数据文件");
                        summaries.Add("游戏路径可能设置无效, 例如位于断开连接的硬盘或网络存储上, 请在设置中检查游戏路径");
                        descriptions.Add(exception.ToString());
                        break;

                    case Win32Exception win32Exception:
                        summaries.Add($"发生未知错误 (0x{(uint)win32Exception.HResult:X8}: {win32Exception.Message})");
                        actionables.Add("请反馈此错误");
                        descriptions.Add(exception.ToString());
                        break;

                    default:
                        summaries.Add($"发生未知错误 ({exception.Message})");
                        actionables.Add("请反馈此错误");
                        descriptions.Add(exception.ToString());
                        break;
                }
            }

            if (exceptions.Count == 1)
            {
                builder.WithText($"{summaries[0]}\n\n{actionables[0]}")
                       .WithDescription(descriptions[0] ?? string.Empty);
            }
            else
            {
                builder.WithText("发生了多个错误");

                for (var i = 0; i < summaries.Count; i++)
                {
                    builder.WithAppendText($"\n{i + 1}. {summaries[i]}\n    => {actionables[i]}");
                    if (string.IsNullOrWhiteSpace(descriptions[i]))
                        continue;
                    builder.WithAppendDescription($"########## Exception {i + 1} ##########\n{descriptions[i]}\n\n");
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
                                    Log.Warning(ex2, "Could not kill process (PID={0}, name={1})", process.Id, process.ProcessName);
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

    #region 注入

    public void StartInject()
    {
        if (Window.Dispatcher != Dispatcher.CurrentDispatcher)
        {
            Window.Dispatcher.Invoke(StartInject);
            return;
        }

        if (IsInjecting || SelectedProcess == null)
            return;

        IsLoadingDialogOpen  = true;
        LoadingDialogMessage = "注入中...";
        IsInjecting          = true;
        CommandManager.InvalidateRequerySuggested();
        
        Task.Run
        (() =>
            {
                try
                {
                    if (!PlatformHelpers.EnsureElevated())
                        return;

                    var gamePid = SelectedProcess.ProcessID;

                    if (SelectedProcess.HasInjected)
                    {
                        CustomMessageBox.Builder
                                        .NewFrom("选定进程已被注入")
                                        .WithButtons(MessageBoxButton.OK)
                                        .WithCaption("XIVLauncherCN (Soil)")
                                        .WithParentWindow(Window)
                                        .Show();
                    }
                    else
                    {
                        if (!InjectGameAndAddon(gamePid)) return;
                        
                        SelectedProcess.HasInjected = true;
                        
                        var dialog = CustomMessageBox.Builder
                                                     .NewFrom("注入完成, 是否要退出 XIVLauncherCN")
                                                     .WithButtons(MessageBoxButton.YesNo)
                                                     .WithCaption("XIVLauncherCN (Soil)")
                                                     .WithParentWindow(Window)
                                                     .Show();

                        if (dialog == MessageBoxResult.Yes)
                        {
                            Log.CloseAndFlush();
                            Environment.Exit(0);
                        }
                    }
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Builder
                                    .NewFromUnexpectedException(ex, "InjectGame")
                                    .WithParentWindow(Window)
                                    .Show();
                }
                finally
                {
                    IsLoadingDialogOpen = false;
                    IsInjecting         = false;
                    CommandManager.InvalidateRequerySuggested();
                    
                    Activate();
                }
            }
        );
    }
    
    public bool InjectGameAndAddon(int gamePid, bool noThird = false, bool noPlugins = false)
    {
        var gameExePath   = Process.GetProcessById(gamePid).MainModule?.FileName;
        var gameExeFolder = Path.GetDirectoryName(gameExePath);
        var gamePath      = new DirectoryInfo(gameExeFolder!).Parent;
        if (gamePath == null)
        {
            CustomMessageBox.Show
            (
                "无法解析游戏目录, 注入失败",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: Window
            );
            return false;
        }

        EnsureDalamudCompatibility();
        
        var dalamudLauncher = CreateDalamudLauncher(gamePath, DalamudLoadMethod.DllInject, noPlugins, noThird);
        var dalamudOk       = EnsureDalamudUpdate(dalamudLauncher, App.Settings.GamePath, true);

        Troubleshooting.LogTroubleshooting();

        if (!dalamudOk)
        {
            CustomMessageBox.Show
            (
                "Dalamud 尚未准备完成, 注入失败",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            return false;
        }

        dalamudLauncher.Inject(gamePid, noPlugins);
        return true;
    }

    #endregion

    #region 进程

    public void StartRefreshFFXIVProcess()
    {
        if (!PlatformHelpers.EnsureElevated())
            return;

        if (ProcessRefreshTask is { IsCompleted: false })
            return;

        ProcessRefreshCancelSource?.Dispose();
        ProcessRefreshCancelSource = new();
        
        var processRefreshToken = ProcessRefreshCancelSource.Token;
        ProcessRefreshTask = Task.Run
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
                            }
                        );

                        Log.Verbose("Refreshing Processes...");
                        await Task.Delay(1000, processRefreshToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
            },
            processRefreshToken
        );
    }
    
    private void StopRefreshFFXIVProcess(bool clearCollection)
    {
        if (ProcessRefreshCancelSource != null)
        {
            ProcessRefreshCancelSource.Cancel();
            ProcessRefreshCancelSource.Dispose();
            ProcessRefreshCancelSource = null;
        }

        ProcessRefreshTask = null;

        if (!clearCollection)
            return;

        foreach (var process in FFXIVProcesses)
            process.Dispose();

        FFXIVProcesses.Clear();
        SelectedProcess = null;
    }
    
    #endregion
    
    #region 启动游戏
    
    public async Task<FFXIVProcess?> StartGameAndAddon(LoginResult loginResult, bool forceNoDalamud, bool noThird, bool noPlugins)
    {
        if (Area == null || Area.AreaID == "-1")
        {
            CustomMessageBox.Show
            (
                "获取服务器信息失败, 无法登录",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: Window
            );
            
            return null;
        }
        
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        var dalamudLauncher = CreateDalamudLauncher
        (
            App.Settings.GamePath,
            App.Settings.InGameAddonLoadMethod.GetValueOrDefault(DalamudLoadMethod.DllInject),
            noPlugins,
            noThird
        );

        var dalamudOk = false;
        EnsureDalamudCompatibility();

        if (App.Settings.InGameAddonEnabled && !forceNoDalamud)
            dalamudOk = EnsureDalamudUpdate(dalamudLauncher, App.Settings.GamePath, false);

        var gameRunner = new WindowsGameRunner(dalamudLauncher, dalamudOk, App.DalamudUpdater.Runtime);
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
            loginResult.DCTravelPort,
            Area.AreaID,
            Area.AreaLobby,
            Area.AreaGM,
            Area.AreaConfigUpload,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(loginResult.Areas))),
            App.Settings.AdditionalLaunchArgs,
            App.Settings.GamePath,
            App.Settings.EncryptArgumentsV2.GetValueOrDefault(true),
            App.Settings.DpiAwareness.GetValueOrDefault(DpiAwareness.Unaware)
        );

        Troubleshooting.LogTroubleshooting();

        if (launched == null)
        {
            Log.Information("GameProcess was null...");
            IsLoggingIn = false;
            return null;
        }

        var addonMgr = new AddonManager();

        try
        {
            App.Settings.AddonList ??= [];

            var addons = App.Settings.AddonList.Where(x => x.IsEnabled).Select(x => x.Addon).Cast<IAddon>().ToList();

            addonMgr.RunAddons(launched.ProcessID, addons);
        }
        catch (Exception ex)
        {
            CustomMessageBox.Builder
                            .NewFrom(ex, "Addons")
                            .WithAppendText("\n\n")
                            .WithAppendText("这可能由杀毒软件引起, 请检查日志并添加必要的排除项")
                            .WithParentWindow(Window)
                            .Show();

            IsLoggingIn = false;

            addonMgr.StopAddons();
        }

        Log.Debug("等待游戏进程退出");

        if (dalamudOk)
        {
            await Launcher.RestartMonitor
                          .MonitorAsync
                          (
                              launched,
                              () => StartGameAndAddon(loginResult, forceNoDalamud, noThird, noPlugins),
                              LoginCancelSource?.Token ?? CancellationToken.None
                          )
                          .ConfigureAwait(false);
        }
        else
            await Task.Run(() => launched.UnderlyingProcess.WaitForExit()).ConfigureAwait(false);

        Log.Verbose("游戏进程已退出");

        if (addonMgr.IsRunning)
            addonMgr.StopAddons();

        try
        {
            DCTravelListener?.Stop();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法关闭 DCTravelListener");
        }

        return launched;
    }
    
    private void FakeStartGame()
    {
        if (Window.Dispatcher != Dispatcher.CurrentDispatcher)
        {
            Window.Dispatcher.Invoke(FakeStartGame);
            return;
        }

        Task.Run
        (() => StartGameAndAddon
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
        var dalamudCompatCheck = new WindowsDalamudCompatibilityCheck();

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
    
    private bool EnsureDalamudUpdate(DalamudLauncher dalamudLauncher, DirectoryInfo gamePath, bool appendWafStatusCodeHint)
    {
        try
        {
            var dalamudStatus = dalamudLauncher.HoldForUpdate(gamePath);
            return dalamudStatus == DalamudLauncher.DalamudInstallState.Ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MainWindow] 尝试更新 Dalamud 时发生错误");

            var ensurementErrorMessage = "下载 Dalamud 相关文件异常\n请检查本地网络连接, 或关闭杀毒软件\n游戏将照常启动, 但无法使用 Dalamud";

            if (appendWafStatusCodeHint
                && ex is HttpRequestException httpRequestException
                && httpRequestException.StatusCode.HasValue
                && (int)httpRequestException.StatusCode is 403 or 444 or 522)
                ensurementErrorMessage = $"服务器错误: {httpRequestException.StatusCode}\n{ensurementErrorMessage}";
            else
                ensurementErrorMessage = $"错误: {ex.Message}\n{ensurementErrorMessage}";

            CustomMessageBox.Builder
                            .NewFrom(ensurementErrorMessage)
                            .WithImage(MessageBoxImage.Warning)
                            .WithButtons(MessageBoxButton.OK)
                            .WithShowHelpLinks()
                            .WithParentWindow(Window)
                            .Show();
            return false;
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
    
    private async Task<bool> RepairGame(LoginResult loginResult)
    {
        var doLogin = false;
        var mutex   = new Mutex(false, "XivLauncherIsPatching");

        if (mutex.WaitOne(0, false))
        {
            Debug.Assert(loginResult.PendingPatches        != null);
            Debug.Assert(loginResult.PendingPatches.Length != 0);

            Log.Information("STARTING REPAIR");

            if (!AppUtil.TryYellOnGameFilesBeingOpen
                (
                    Window,
                    n => n switch
                    {
                        1 => "关闭以下应用程序以修复游戏",
                        _ => "关闭以下应用程序以修复游戏"
                    }
                ))
                return false;

            using var verify = new PatchVerifier(CommonSettings.Instance, loginResult, TimeSpan.FromMilliseconds(100), Constants.MaxExpansion);

            Hide();
            IsEnabled = false;

            var progressDialog = (GameRepairProgressWindow)Window.Dispatcher.Invoke
            (
                new Func<PatchVerifier, GameRepairProgressWindow>(CreateGameRepairProgressWindow),
                verify
            );

            for (var doVerify = true; doVerify;)
            {
                progressDialog.Dispatcher.Invoke(progressDialog.Show);

                verify.Start();
                await verify.WaitForCompletion().ConfigureAwait(false);

                progressDialog.Dispatcher.Invoke(progressDialog.Hide);

                switch (verify.State)
                {
                    case PatchVerifier.VerifyState.Done:
                        switch (CustomMessageBox.Builder
                                                .NewFrom
                                                (
                                                    verify.NumBrokenFiles switch
                                                    {
                                                        0 => "所有游戏文件似乎都是完整的",
                                                        1 => "XIVLauncher 已成功修复 1 个游戏文件",
                                                        _ => $"XIVLauncher 已成功修复 {verify.NumBrokenFiles} 个游戏文件"
                                                    }
                                                )
                                                .WithAppendText
                                                (
                                                    verify.MovedFiles.Count switch
                                                    {
                                                        0 => "",
                                                        1 => $"\n\n此外, 1 个非原始游戏安装文件已被移动到 {verify.MovedFileToDir}\n如果使用了 ReShade, 需要重新安装",
                                                        _ => $"\n\n此外, {verify.MovedFiles.Count} 个非原始游戏安装文件已被移动到 {verify.MovedFileToDir}\n如果使用了 ReShade, 需要重新安装"
                                                    }
                                                )
                                                .WithDescription(verify.MovedFiles.Any() ? string.Join("\n", verify.MovedFiles.Select(x => $"* {x}")) : string.Empty)
                                                .WithImage(MessageBoxImage.Information)
                                                .WithButtons(MessageBoxButton.YesNoCancel)
                                                .WithYesButtonText("启动游戏 (_L)")
                                                .WithNoButtonText("再次验证 (_V)")
                                                .WithCancelButtonText("关闭 (_C)")
                                                .WithParentWindow(Window)
                                                .Show())
                        {
                            case MessageBoxResult.Yes:
                                doLogin  = true;
                                doVerify = false;
                                break;

                            case MessageBoxResult.No:
                                doLogin  = false;
                                doVerify = true;
                                break;

                            case MessageBoxResult.Cancel:
                                doLogin = doVerify = false;
                                break;
                        }

                        break;

                    case PatchVerifier.VerifyState.Error:
                        doLogin = false;

                        if (verify.LastException is NoVersionReferenceException)
                        {
                            doVerify = CustomMessageBox.Builder
                                                       .NewFrom
                                                       (
                                                           "当前游戏版本暂无法通过 XIVLauncher 修复, 参考信息尚不可用\n请稍后重试"
                                                       )
                                                       .WithImage(MessageBoxImage.Exclamation)
                                                       .WithButtons(MessageBoxButton.OKCancel)
                                                       .WithOkButtonText("重试 (_T)")
                                                       .WithParentWindow(Window)
                                                       .Show()
                                       == MessageBoxResult.OK;
                        }
                        // Seemingly no better way to detect this, probably brittle if this is localized
                        else if (verify.LastException != null && verify.LastException.ToString().Contains("Data error"))
                        {
                            doVerify = new CustomMessageBox.Builder()
                                       .WithText("硬盘在检查游戏文件时报告错误, XIVLauncher 无法修复此安装, 错误可能表明硬盘存在物理问题\n请检查硬盘健康状况或尝试更新固件\n将游戏重新安装到新位置可能暂时解决此问题")
                                       .WithExitOnClose(CustomMessageBox.ExitOnCloseModes.DontExitOnClose)
                                       .WithImage(MessageBoxImage.Error)
                                       .WithShowHelpLinks()
                                       .WithShowDiscordLink()
                                       .WithShowNewGitHubIssue(false)
                                       .WithButtons(MessageBoxButton.OKCancel)
                                       .WithOkButtonText("重试 (_T)")
                                       .WithParentWindow(Window)
                                       .Show()
                                       == MessageBoxResult.OK;
                        }
                        else
                        {
                            if (verify.LastException == null)
                            {
                                doVerify = new CustomMessageBox.Builder()
                                           .WithText("修复游戏文件时发生错误\n可能需要重新安装游戏")
                                           .WithImage(MessageBoxImage.Exclamation)
                                           .WithButtons(MessageBoxButton.OKCancel)
                                           .WithOkButtonText("重试 (_T)")
                                           .WithParentWindow(Window)
                                           .Show()
                                           == MessageBoxResult.OK;
                            }
                            else
                            {
                                doVerify = CustomMessageBox.Builder
                                                           .NewFrom(verify.LastException, "PatchVerifier")
                                                           .WithAppendText("\n\n")
                                                           .WithAppendText("修复游戏文件时发生错误\n可能需要重新安装游戏")
                                                           .WithImage(MessageBoxImage.Exclamation)
                                                           .WithButtons(MessageBoxButton.OKCancel)
                                                           .WithOkButtonText("重试 (_T)")
                                                           .WithParentWindow(Window)
                                                           .Show()
                                           == MessageBoxResult.OK;
                            }
                        }

                        break;

                    case PatchVerifier.VerifyState.Cancelled:
                        doLogin = doVerify = false;
                        break;
                }
            }

            progressDialog.Dispatcher.Invoke(progressDialog.Close);
            mutex.Close();
            mutex = null;
        }
        else
        {
            CustomMessageBox.Show
            (
                "XIVLauncher 正在另一个进程中更新游戏, 请检查是否已打开多个 XIVLauncher",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: Window
            );
        }

        return doLogin;
    }

    private Task<bool> InstallGamePatch(LoginResult loginResult)
    {
        var pendingPatches = PatchExecutionCoordinator.GetPendingPatchesForInstall(loginResult);
        return HandlePatchAsync(Repository.Ffxiv, pendingPatches, loginResult.UniqueID ?? string.Empty);
    }

    private async Task<bool> HandlePatchAsync(Repository repository, PatchListEntry[] pendingPatches, string sid)
    {
        using var installer = new Common.Game.Patch.PatchInstaller(App.Settings.KeepPatches ?? false);
        
        var patcher = new PatchManager
        (
            App.Settings.PatchAcquisitionMethod ?? AcquisitionMethod.Aria,
            App.Settings.SpeedLimitBytes,
            repository,
            pendingPatches,
            App.Settings.GamePath,
            App.Settings.PatchPath,
            installer,
            Launcher,
            sid
        );
        patcher.OnFail   += OnPatcherFail;
        installer.OnFail += OnInstallerFail;

        Hide();

        var progressDialog = Window.Dispatcher.Invoke
        (() =>
            {
                var d = new PatchDownloadDialog(patcher);
                if (Window.IsVisible)
                    d.Owner = Window;
                d.Show();
                d.Activate();
                return d;
            }
        );

        try
        {
            var result = await PatchExecutionCoordinator.ExecuteAsync
                         (
                             new PatchExecutionRequest
                             {
                                 MutexName   = "XivLauncherIsPatching",
                                 Patcher     = patcher,
                                 AriaLogFile = new FileInfo(Path.Combine(Paths.RoamingPath, "aria2.log")),
                                 IsGameOpen  = GameHelpers.CheckIsGameOpen,
                                 ContinueWhenGameOpen = () => CustomMessageBox.Builder
                                                                              .NewFrom("游戏和/或官方启动器正在运行, 在这种情况下 XIVLauncher 无法更新游戏\n请关闭官方启动器后重试")
                                                                              .WithImage(MessageBoxImage.Exclamation)
                                                                              .WithButtons(MessageBoxButton.OKCancel)
                                                                              .WithOkButtonText("刷新 (_R)")
                                                                              .WithDefaultResult(MessageBoxResult.OK)
                                                                              .Show()
                                                              != MessageBoxResult.Cancel,
                                 EnsureGameFilesClosed = () => AppUtil.TryYellOnGameFilesBeingOpen
                                 (
                                     Window,
                                     n => n switch
                                     {
                                         1 => "关闭以下应用程序以更新游戏",
                                         _ => "关闭以下应用程序以更新游戏"
                                     }
                                 )
                             }
                         ).ConfigureAwait(false);

            switch (result.Status)
            {
                case PatchExecutionStatus.Success:
                    return true;

                case PatchExecutionStatus.AlreadyRunning:
                    CustomMessageBox.Show
                    (
                        "XIVLauncher 正在另一个进程中更新游戏, 请检查是否已打开多个 XIVLauncher",
                        "XIVLauncherCN (Soil)",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        parentWindow: Window
                    );
                    Environment.Exit(0);
                    return false;

                case PatchExecutionStatus.CancelledByUser:
                    return false;

                case PatchExecutionStatus.PatchInstallerError:
                    CustomMessageBox.Show
                    (
                        $"补丁安装程序无法正确启动\n{result.Exception?.Message}\n\n如果拒绝了访问权限, 请重试\n如果问题仍然存在, 请通过 Discord 联系我们",
                        "XIVLauncher Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        parentWindow: Window
                    );
                    return false;

                case PatchExecutionStatus.NotEnoughSpace:

                    if (result.Exception is NotEnoughSpaceException sex)
                    {
                        var bytesRequired = APIHelper.BytesToString(sex.BytesRequired);
                        var bytesFree     = APIHelper.BytesToString(sex.BytesFree);

                        switch (sex.Kind)
                        {
                            case NotEnoughSpaceException.SpaceKind.Patches:
                                CustomMessageBox.Show
                                (
                                    $"磁盘空间不足, 无法下载补丁\n\n可以在设置中更改补丁下载位置\n\n需要: {bytesRequired}\n可用: {bytesFree}",
                                    "XIVLauncher Error",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error,
                                    parentWindow: Window
                                );
                                break;

                            case NotEnoughSpaceException.SpaceKind.AllPatches:
                                CustomMessageBox.Show
                                (
                                    $"磁盘空间不足, 无法下载所有补丁\n\n可以在 XIVLauncher 设置中更改补丁下载位置\n\n需要: {bytesRequired}\n可用: {bytesFree}",
                                    "XIVLauncher Error",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error,
                                    parentWindow: Window
                                );
                                break;

                            case NotEnoughSpaceException.SpaceKind.Game:
                                CustomMessageBox.Show
                                (
                                    $"磁盘空间不足, 无法安装补丁\n\n可以在设置中更改游戏安装位置\n\n需要: {bytesRequired}\n可用: {bytesFree}",
                                    "XIVLauncher Error",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error,
                                    parentWindow: Window
                                );
                                break;

                            default:
                                Debug.Assert(false, "HandlePatchAsync:Invalid NotEnoughSpaceException.SpaceKind value.");
                                break;
                        }
                    }

                    return false;

                default:
                    if (result.Exception == null)
                        CustomMessageBox.Show("补丁流程发生未知错误", "XIVLauncher Error", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: Window);
                    else
                    {
                        CustomMessageBox.Builder.NewFromUnexpectedException(result.Exception, "HandlePatchAsync")
                                        .WithParentWindow(Window)
                                        .Show();
                    }

                    return false;
            }
        }
        finally
        {
            progressDialog.Dispatcher.Invoke
            (() =>
                {
                    progressDialog.Hide();
                    progressDialog.Close();
                }
            );
        }
    }

    #endregion

    #region 杂项

    private static DalamudLauncher CreateDalamudLauncher(DirectoryInfo gamePath, DalamudLoadMethod loadMethod, bool noPlugins, bool noThird) =>
        new
        (
            new WindowsDalamudRunner(),
            App.DalamudUpdater,
            loadMethod,
            gamePath,
            new DirectoryInfo(Paths.RoamingPath),
            new DirectoryInfo(Paths.RoamingPath),
            ClientLanguage.ChineseSimplified,
            (int)App.Settings.DalamudInjectionDelayMs,
            false,
            noPlugins,
            noThird,
            Troubleshooting.GetTroubleshootingJson()
        );

    private Action<object> CreateLoginHandler(LoginAfterAction action) =>
        _ =>
        {
            if (action == LoginAfterAction.CancelLogin)
            {
                CancelLogin();
                return;
            }

            if (IsLoggingIn)
                return;

            if (action == LoginAfterAction.Start) LoginMessage = string.Empty;

            if (IsAutoLogin && !App.Settings.HasShownAutoLaunchDisclaimer.GetValueOrDefault(false))
            {
                CustomMessageBox.Builder
                                .NewFrom("自动登录已启用, 后续将默认使用当前账号登录, 并不再显示主窗口\n若需要修改设置, 请在登录时按住 SHIFT 键")
                                .WithParentWindow(Window)
                                .Show();

                App.Settings.HasShownAutoLaunchDisclaimer = true;
            }

            if (GameHelpers.CheckIsGameOpen() && action == LoginAfterAction.Repair)
            {
                CustomMessageBox.Builder
                                .NewFrom("官方启动器或游戏正在运行中, 无法执行修复, 请关闭相关进程后重试")
                                .WithImage(MessageBoxImage.Exclamation)
                                .WithParentWindow(Window)
                                .Show();

                return;
            }

            if (action == LoginAfterAction.Repair)
            {
                var res = CustomMessageBox.Builder
                                          .NewFrom("XIVLauncher 将会搜寻任何与原版不一致的游戏文件并替换修复\n这可能导致使用 TexTools 安装的模组被还原\n请确认")
                                          .WithButtons(MessageBoxButton.YesNo)
                                          .WithImage(MessageBoxImage.Question)
                                          .WithParentWindow(Window)
                                          .Show();

                if (res == MessageBoxResult.No)
                    return;
            }

            StartLogin(LoginTypeOption.LoginType, Username, Password, IsFastLogin, IsReadWegameInfo, action);
        };
    
    private GameRepairProgressWindow CreateGameRepairProgressWindow(PatchVerifier verify)
    {
        var dialog = new GameRepairProgressWindow(verify);
        if (Window.IsVisible)
            dialog.Owner = Window;
        dialog.Show();
        dialog.Activate();
        return dialog;
    }
    
    #endregion

    #region 事件

    private void OnPropertyChanged(string propertyName)
    {
        var handler = PropertyChanged;
        handler?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void OnWindowClosed(object sender, object args)
    {
        StopRefreshFFXIVProcess(true);
        CancelLogin();
        Application.Current.Shutdown();
    }

    public void OnWindowClosing(object sender, CancelEventArgs args)
    {
        if (!IsLoggingIn) return;
        args.Cancel = true;
    }
    
    private void OnPatcherFail(PatchListEntry patch, string context)
    {
        CustomMessageBox.Show
        (
            "安装更新时发生异常, 请重试或使用官方启动器完成游戏更新",
            "XIVLauncherCN (Soil)",
            MessageBoxButton.OK,
            MessageBoxImage.Error
        );
        
        Environment.Exit(0);
    }

    private void OnInstallerFail()
    {
        CustomMessageBox.Show
        (
            "更新程序发生异常, 请重试或使用官方启动器完成游戏更新",
            "XIVLauncherCN (Soil)",
            MessageBoxButton.OK,
            MessageBoxImage.Error
        );

        Environment.Exit(0);
    }

    #endregion

    #region Commands

    public ICommand StartLoginCommand       { get; set; }
    public ICommand LoginNoStartCommand     { get; set; }
    public ICommand LoginNoDalamudCommand   { get; set; }
    public ICommand LoginNoPluginsCommand   { get; set; }
    public ICommand LoginNoThirdCommand     { get; set; }
    public ICommand LoginRepairCommand      { get; set; }
    public ICommand LoginCancelCommand      { get; set; }
    public ICommand LoginForceQRCommand     { get; set; }
    public ICommand InjectModeSwitchCommand { get; set; }
    public ICommand InjectGameCommand       { get; set; }
    public ICommand FakeStartCommand        { get; set; }

    #endregion

    #region Bindings

    public bool IsAutoLogin
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(IsAutoLogin));
        }
    }

    public bool IsFastLogin
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(IsFastLogin));
        }
    }

    public bool IsReadWegameInfo
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(IsReadWegameInfo));
        }
    }

    public string Username
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(Username));
        }
    } = string.Empty;

    public LoginTypeOption LoginTypeOption
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(LoginTypeOption));
        }
    } = null!;

    public int AreaIndex
    {
        set => App.Settings.SelectedServer = value;
    }

    public LoginArea? Area
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(Area));
            
            Log.Information("大区变更 {OldArea} -> {NewArea}", field, value);
        }
    }

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
    }

    public bool IsLoadingDialogOpen
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(IsLoadingDialogOpen));
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

    public string LoginMessage
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(LoginMessage));
        }
    } = string.Empty;

    public SolidColorBrush WorldStatusIconColor
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(WorldStatusIconColor));
        }
    }

    public BitmapImage? QRCodeBitmapImage
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(QRCodeBitmapImage));
        }
    }

    public PackIconKind ModeSwitchIcon
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(ModeSwitchIcon));
        }
    }

    public ObservableCollection<FFXIVProcess> FFXIVProcesses { get; } = [];

    public FFXIVProcess? SelectedProcess
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;

            field = value;
            OnPropertyChanged(nameof(SelectedProcess));
            OnPropertyChanged(nameof(CanOperateOnSelectedProcess));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasAvailableProcesses => FFXIVProcesses.Count > 0;

    public bool CanOperateOnSelectedProcess => SelectedProcess != null;

    public string ProcessSelectionHint => HasAvailableProcesses ? "选择要注入的进程" : "未检测到可注入进程";

    public LoginArea[] LoginAreas
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(LoginAreas));
        }
    } = [];

    #endregion

    public enum LoginCardType
    {
        Logining   = 0,
        MainPage   = 1,
        ScanQRCode = 2,
        InjectMode = 3
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
