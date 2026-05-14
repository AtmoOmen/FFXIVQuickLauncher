using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
using System.Windows.Threading;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Common;
using XIVLauncher.Common.Addon;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.DCTravel;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;
using XIVLauncher.Common.Windows;
using XIVLauncher.Game;
using XIVLauncher.GamePatchV3;
using XIVLauncher.GamePatchV3.Update;
using XIVLauncher.Support;
using XIVLauncher.Windows.GameClientFiles;
using XIVLauncher.Windows.Services;
using XIVLauncher.Windows.ViewModel.MainWindow;
using XIVLauncher.Windows.ViewModel.MainWindow.Factories;
using XIVLauncher.Windows.ViewModel.MainWindow.Providers;
using XIVLauncher.Windows.ViewModel.MainWindow.Services;
using XIVLauncher.Xaml;
using DCTravelClient = XIVLauncher.Common.Game.DCTravel.DCTravelClient;

namespace XIVLauncher.Windows.ViewModel;

internal class MainWindowViewModel : INotifyPropertyChanged
{
    public const string PRESUDO_PASSWORD = "********假的密码********";

    public SettingsWindowViewModel Settings { get; }

    public LoginPageViewModel LoginPage { get; }

    public InjectPageViewModel InjectPage { get; }

    public ICommand AccountSwitcherButtonCommand { get; }

    public ICommand RefreshDalamudInfoCommand => refreshDalamudInfoCommand;

    public AccountSwitcherViewModel AccountSwitcher { get; }

    public Launcher          Launcher         { get; private set; }
    public AccountManager    AccountManager   { get; private set; } = App.AccountManager;
    public DCTravelListener? DCTravelListener { get; private set; }
    public Window            Window           { get; private set; }

    public Action Activate        { get; set; } = null!;
    public Action Hide            { get; set; } = null!;
    public Action ReloadHeadlines { get; set; } = null!;

    public bool IsLoggingIn { get; set; }

    private          DalamudLauncherFactory        DalamudLauncherFactory    { get; }
    private          MainWindowDialogProvider      DialogProvider            { get; }
    private          MainWindowAccountDraftFactory AccountDraftFactory       { get; }
    private          GameLaunchService             GameLaunchService         { get; }
    private          GameClientFileTaskService     GameClientFileTaskService { get; }
    private readonly SyncCommand                   refreshDalamudInfoCommand;
    private          CancellationTokenSource?      LoginCancelSource        { get; set; }
    private          bool                          IsLoginCanceledByUser    { get; set; }
    private          LoginCardType?                LoginCardAfterCompletion { get; set; }

    public MainWindowViewModel(Window window)
    {
        Window                    = window;
        Settings                  = new SettingsWindowViewModel(new DialogService(window), new ExternalLaunchService());
        DalamudLauncherFactory    = new DalamudLauncherFactory();
        DialogProvider            = new MainWindowDialogProvider(window);
        AccountDraftFactory       = new MainWindowAccountDraftFactory();
        Launcher                  = new();
        GameLaunchService         = new GameLaunchService(window);
        GameClientFileTaskService = new GameClientFileTaskService(window);
        AccountSwitcher = new AccountSwitcherViewModel
        (
            AccountManager,
            new DialogService(window),
            new ShortcutService(),
            () => CloseAccountSwitcher(false)
        );
        AccountSwitcherButtonCommand = new SyncCommand(ExecuteAccountSwitcherButton);
        refreshDalamudInfoCommand    = new SyncCommand(_ => RefreshDalamudInfo(), () => Settings.EnableHooks && App.DalamudUpdater.State != DalamudUpdater.DownloadState.Unknown);
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
            () => SwitchCard(LoginCardType.MainPage)
        );
        LoginPage.RefreshCommandStates();
        InjectPage.RefreshCommandStates();
        UpdateDalamudStatusText();
        App.DalamudUpdater.StatusChanged += DalamudUpdaterStatusChanged;
        Settings.SettingsSaved += (_, _) =>
        {
            InjectPage.ReloadSettings();
            RefreshDalamudInfoCommandState();
        };
    }

    public bool IsAccountSwitcherVisible => IsAccountSwitcherOpen;

    public void CloseAccountSwitcher(bool animate) =>
        IsAccountSwitcherOpen = false;

    #region 界面控制

    public void SwitchCard(LoginCardType i, bool shouldCancelLogin = true) =>
        Window.Dispatcher.Invoke
        (() =>
            {
                if (shouldCancelLogin)
                    CancelLogin();
                LoginCardTransitionerIndex = (int)i;

                InjectPage.SetActive(LoginCardTransitionerIndex == (int)LoginCardType.InjectMode);
            }
        );

    private void ExecuteAccountSwitcherButton(object parameter) =>
        IsAccountSwitcherOpen ^= true;

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
                    await LoginAsync(loginType, username, password, doingAutoLogin, readWeGameInfo, action).ConfigureAwait(false);
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
        bool             doingAutoLogin,
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

        var         finalLoginType             = loginType;
        var         secret                     = string.Empty;
        var         savedAccount               = FindSavedAccount(loginType, username);
        var         accountType                = ResolveAccountType(loginType, savedAccount);
        var         hasUnavailableSavedSecrets = AccountManager.HasUnavailableSecrets(savedAccount);
        XIVAccount? pendingNewAccount          = null;

        try
        {
            inputPassword = inputPassword == PRESUDO_PASSWORD ? string.Empty : inputPassword?.Trim() ?? string.Empty;

            switch (loginType)
            {
                case LoginType.Static:
                    if (!string.IsNullOrEmpty(inputPassword))
                        secret = inputPassword;
                    else if (!hasUnavailableSavedSecrets && savedAccount?.SdoPassword is { Length: > 0 } savedPassword)
                        secret = await AccountManager.Decrypt(savedPassword);

                    ArgumentException.ThrowIfNullOrEmpty(username, "静态登录用户名");
                    ArgumentException.ThrowIfNullOrEmpty(secret,   "静态登录密码");
                    finalLoginType = LoginType.Static;
                    break;

                case LoginType.WeGameAuto:
                    doingAutoLogin = true;

                    if (readWeGameInfo)
                    {
                        var loginData = await ReadWeGameAccountInfoAsync();
                        if (loginData == null)
                            return;

                        if (string.IsNullOrEmpty(loginData.SndaID) || string.IsNullOrEmpty(loginData.SessionId))
                            throw new Exception("获取WeGame登录信息失败");
                        username = loginData.SndaID;
                        secret   = loginData.SessionId;
                        var areaId = loginData.Args.Where(x => x.Contains("AreaID=")).Select(x => x.Split('=')[1]).First();
                        LoginPage.Area = LoginPage.LoginAreas.FirstOrDefault(x => x.AreaID == areaId) ?? LoginPage.Area;
                    }
                    else
                    {
                        ArgumentException.ThrowIfNullOrEmpty(username, "已保存账号的 SndaID");

                        if (savedAccount == null)
                        {
                            CustomMessageBox.Show
                            (
                                "未找到该 SndaID 对应的已保存账号, 请勾选 \"重新从 WeGame 读取 SID\" 后再试",
                                "XIVLauncherCN (Soil)",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error,
                                parentWindow: Window
                            );
                            return;
                        }

                        if (hasUnavailableSavedSecrets || string.IsNullOrWhiteSpace(savedAccount.WeGameSIDSecret))
                        {
                            CustomMessageBox.Show
                            (
                                "该账号没有可用的已保存 SID, 请勾选 \"重新从 WeGame 读取 SID\" 后再试",
                                "XIVLauncherCN (Soil)",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error,
                                parentWindow: Window
                            );
                            return;
                        }

                        secret = await AccountManager.Decrypt(savedAccount.WeGameSIDSecret);
                    }

                    finalLoginType = LoginType.WeGameAuto;
                    savedAccount   = FindSavedAccount(finalLoginType, username);
                    accountType    = ResolveAccountType(finalLoginType, savedAccount);
                    break;

                case LoginType.WeGameManual:
                    if (!hasUnavailableSavedSecrets && string.IsNullOrEmpty(inputPassword) && savedAccount?.WeGameTokenSecret is { Length: > 0 } weGameTokenSecret)
                    {
                        secret         = await AccountManager.CredProvider.Decrypt(weGameTokenSecret);
                        finalLoginType = LoginType.WeGameManual;
                    }

                    if (string.IsNullOrEmpty(secret))
                    {
                        secret         = inputPassword;
                        finalLoginType = LoginType.WeGameManual;
                    }

                    ArgumentException.ThrowIfNullOrEmpty(secret, "WeGame 自动登录密钥");
                    break;

                case LoginType.Slide:
                    if (!hasUnavailableSavedSecrets && doingAutoLogin && savedAccount?.SdoAutoLoginSessionKey is { Length: > 0 } slideAutoLoginSessionKey)
                    {
                        secret         = await AccountManager.Decrypt(slideAutoLoginSessionKey);
                        finalLoginType = LoginType.AutoLoginSession;
                    }

                    if (string.IsNullOrEmpty(secret))
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

        var requiresNewAccountDeviceProfileSetup   = ShouldRequireNewAccountDeviceProfileSetup(savedAccount);
        var shouldRequestTemporaryAutoLoginSession = requiresNewAccountDeviceProfileSetup && loginType == LoginType.QRCode;
        var loginAutoLogin                         = doingAutoLogin || shouldRequestTemporaryAutoLoginSession;

        ResolvedDeviceProfile resolvedDeviceProfile;

        if (requiresNewAccountDeviceProfileSetup && loginType != LoginType.QRCode)
        {
            pendingNewAccount = MainWindowAccountDraftFactory.CreatePendingNewAccount(username, username, accountType, LoginPage.Area);

            switch (DialogProvider.PromptNewAccountDeviceProfileChoice())
            {
                case MessageBoxResult.Yes:
                    resolvedDeviceProfile = AccountManager.ResolveDeviceProfile(pendingNewAccount);
                    break;

                case MessageBoxResult.No:
                {
                    var configuredNewAccount = MainWindowAccountDraftFactory.CreateIndependentDeviceProfileDraft(pendingNewAccount);

                    if (!DialogProvider.ShowTemporaryAccountDeviceProfileSettings(configuredNewAccount, AccountManager))
                    {
                        SavePendingNewAccountWithoutSecrets(pendingNewAccount);
                        return;
                    }

                    pendingNewAccount     = configuredNewAccount;
                    resolvedDeviceProfile = AccountManager.ResolveDeviceProfile(configuredNewAccount);
                    break;
                }

                default:
                    return;
            }
        }
        else
            resolvedDeviceProfile = AccountManager.ResolveDeviceProfile(username, accountType);

        var deviceProfileSnapshot = resolvedDeviceProfile.Snapshot;

        var dcTraveler = new DCTravelClient(string.Empty)
        {
            SetSdoAreaFunc = name =>
            {
                App.AccountManager.CurrentAccount!.AreaName = name;
                App.AccountManager.Save();
            }
        };

        var loginResult = await LoginToGameAsync
                          (
                              finalLoginType,
                              loginType,
                              username,
                              secret!,
                              loginAutoLogin,
                              deviceProfileSnapshot,
                              dcTraveler
                          ).ConfigureAwait(false);
        if (loginResult == null)
            return;

        loginResult.Area  = LoginPage.Area;
        loginResult.Areas = LoginPage.LoginAreas;

        var oAuthLogin = loginResult.OAuthLogin;

        if (requiresNewAccountDeviceProfileSetup && loginType == LoginType.QRCode && loginResult.State == LoginState.Ok && oAuthLogin != null)
        {
            pendingNewAccount ??= MainWindowAccountDraftFactory.CreatePendingNewAccount(oAuthLogin.InputUserID, oAuthLogin.SndaID, accountType, LoginPage.Area);

            switch (DialogProvider.PromptNewAccountDeviceProfileChoice())
            {
                case MessageBoxResult.Yes:
                    resolvedDeviceProfile = AccountManager.ResolveDeviceProfile(pendingNewAccount);
                    deviceProfileSnapshot = resolvedDeviceProfile.Snapshot;
                    break;

                case MessageBoxResult.No:
                {
                    var configuredNewAccount = MainWindowAccountDraftFactory.CreateIndependentDeviceProfileDraft(pendingNewAccount);

                    if (!DialogProvider.ShowTemporaryAccountDeviceProfileSettings(configuredNewAccount, AccountManager))
                    {
                        SavePendingNewAccountWithoutSecrets(pendingNewAccount);
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(oAuthLogin.AutoLoginSessionKey))
                    {
                        CustomMessageBox.Show
                        (
                            "首次扫码登录未能获取可用于设备信息重登的会话密钥，本次无法继续启动游戏",
                            "XIVLauncherCN (Soil)",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error,
                            parentWindow: Window
                        );
                        return;
                    }

                    pendingNewAccount     = configuredNewAccount;
                    resolvedDeviceProfile = AccountManager.ResolveDeviceProfile(configuredNewAccount);
                    deviceProfileSnapshot = resolvedDeviceProfile.Snapshot;

                    var reloginResult = await LoginToGameAsync
                                        (
                                            LoginType.AutoLoginSession,
                                            LoginType.AutoLoginSession,
                                            oAuthLogin.InputUserID,
                                            oAuthLogin.AutoLoginSessionKey,
                                            true,
                                            deviceProfileSnapshot,
                                            dcTraveler
                                        ).ConfigureAwait(false);
                    if (reloginResult == null)
                        return;

                    loginResult       = reloginResult;
                    loginResult.Area  = LoginPage.Area;
                    loginResult.Areas = LoginPage.LoginAreas;
                    oAuthLogin        = loginResult.OAuthLogin;
                    break;
                }

                default:
                    return;
            }
        }

        if (loginResult.State == LoginState.Ok)
        {
            var deviceProfileAccount = pendingNewAccount ?? savedAccount;

            if (App.Settings.DalamudEnabled && loginType != LoginType.WeGameAuto)
            {
                try
                {
                    DCTravelListener = null;
                    Log.Information("[DCTravelListener] 正在开启监听用端口");
                    await dcTraveler.GetValidCookie();
                    _ = dcTraveler.KeepCookieAlive();

                    loginResult.DCTravelPort = APIHelper.GetAvailablePort();
                    DCTravelListener         = new(dcTraveler, loginResult.DCTravelPort, false);
                    Log.Information("[DCTravelListener] 打开监听端口: {LoginResultDCTravelPort}", loginResult.DCTravelPort);
                    _ = DCTravelListener.StartAsync();
                }
                catch (Exception ex)
                {
                    loginResult.DCTravelPort = 0;
                    DCTravelListener         = null;
                    Log.Warning(ex, "[DCTravelListener] 启动失败, 已跳过游戏内超域传送支持");
                }
            }

            var accountToSave = new XIVAccount
            {
                AutoLogin                          = loginType == LoginType.WeGameAuto || doingAutoLogin,
                SdoLoginAccount                    = oAuthLogin?.InputUserID!,
                WeGameSndaID                       = oAuthLogin?.SndaID!,
                AccountType                        = accountType,
                AreaName                           = LoginPage.Area!.AreaName,
                UserDefinedName                    = deviceProfileAccount?.UserDefinedName                    ?? null!,
                DeviceProfilePresetId              = deviceProfileAccount?.DeviceProfilePresetId              ?? string.Empty,
                DeviceProfileDynamicEnabled        = deviceProfileAccount?.DeviceProfileDynamicEnabled        ?? false,
                IsDeviceProfileRotation            = deviceProfileAccount?.IsDeviceProfileRotation            ?? true,
                DeviceProfileRotationDays          = deviceProfileAccount?.DeviceProfileRotationDays          ?? AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS,
                DeviceProfileLastGeneratedUtcTicks = deviceProfileAccount?.DeviceProfileLastGeneratedUtcTicks ?? 0
            };

            AccountManager.ApplyResolvedDeviceProfile(accountToSave, resolvedDeviceProfile);

            if (doingAutoLogin && accountToSave.AccountType == XIVAccountType.Sdo)
            {
                if (!string.IsNullOrEmpty(oAuthLogin?.AutoLoginSessionKey))
                    accountToSave.SdoAutoLoginSessionKey = await AccountManager.Encrypt(oAuthLogin.AutoLoginSessionKey);

                var dcTravelListener = DCTravelListener;

                if (dcTravelListener != null && !string.IsNullOrEmpty(oAuthLogin?.AutoLoginSessionKey))
                {
                    dcTravelListener.DCTravelClient.RefreshGameSessionIDByAutoLoginFunc = async () =>
                    {
                        var newLoginResult = await Launcher.LoginClient.LoginBySessionKey
                                             (
                                                 username,
                                                 oAuthLogin.AutoLoginSessionKey,
                                                 dcTravelListener.DCTravelClient,
                                                 deviceProfileSnapshot
                                             ).ConfigureAwait(false);
                        return newLoginResult.OAuthLogin?.SessionID ?? string.Empty;
                    };
                }

                if (finalLoginType == LoginType.Static)
                    accountToSave.SdoPassword = await AccountManager.Encrypt(secret);
            }

            if (finalLoginType == LoginType.WeGameAuto)
                accountToSave.WeGameSIDSecret = await AccountManager.Encrypt(secret);
            else if (finalLoginType == LoginType.WeGameManual)
                accountToSave.WeGameTokenSecret = await AccountManager.Encrypt(secret);

            accountToSave.GenerateID();
            AccountManager.AddAccount(accountToSave);
            AccountManager.CurrentAccount = accountToSave;
            AccountManager.Save();
        }

        Log.Information
        (
            "[MainWindow] 登录结果: {State} {Playable}",
            loginResult.State,
            oAuthLogin?.Playable
        );

        await AccountManager.CredProvider.ClearCache();
        secret = string.Empty;

        if (await ProcessLoginResultAsync(loginResult, action).ConfigureAwait(false))
        {
            if (App.Settings.ExitLauncherWhenGameExit)
                Environment.Exit(0);

            Activate();
        }
    }

    private async Task<GameArgumentInterop.LoginData?> ReadWeGameAccountInfoAsync()
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
            LoginPage.LoginMessage = "请使用 WeGame 登录需要读取的游戏账号并启动游戏";
#if DEBUG
            newPidList = FFXIVProcess.GetGameProcessIDs().ToArray();
#endif
            if (newPidList.Length == 0)
                continue;
            var pid = newPidList.First();
            await argReader.OpenProcess(pid);
            var data = await argReader.ReadArgs();
#if DEBUG
            LoginPage.LoginMessage = "读取成功";
#endif
            argReader.Stop(true);
            return data;
        }
    }

    private async Task<LoginResult?> LoginToGameAsync
    (
        LoginType             type,
        LoginType             fallbackLoginType,
        string                username,
        string                secret,
        bool                  autoLogin,
        DeviceProfileSnapshot deviceProfile,
        DCTravelClient        dcTravelClient
    )
    {
        if (IsLoginCanceledByUser)
            return null;

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

            return null;
        }

        try
        {
            LoginCancelSource?.Dispose();
            LoginCancelSource = new();
            var loginCts = LoginCancelSource;
            var gamePath = App.Settings.GamePath;
            return await Launcher.LoginClient.LoginWithPatchCheck
                   (
                       async ct =>
                       {
                           var checkResult = await GameUpdater.Check(gamePath, false, ct).ConfigureAwait(false);
                           return checkResult.NeedsUpdate
                                      ? new LoginResult { State = LoginState.NeedsPatchGame, OAuthLogin = new() }
                                      : new LoginResult { State = LoginState.Ok, OAuthLogin             = null };
                       },
                       type,
                       fallbackLoginType,
                       requestLoginType => LoginRequest.Create
                       (
                           username,
                           secret,
                           autoLogin,
                           deviceProfile,
                           dcTravelClient,
                           loginCts,
                           qrBytes =>
                           {
                               if (requestLoginType != LoginType.QRCode)
                                   return;

                               Window.Dispatcher.Invoke
                               (() =>
                                   {
                                       LoginPage.QRCodeBitmapImage = qrBytes.ToBitmapImage();
                                       LoginPage.IsQrCodeExpired   = false;
                                   }
                               );
                           },
                           code =>
                           {
                               if (requestLoginType != LoginType.Slide)
                                   return;

                               Log.Information("[MainWindow] 接收到叨鱼确认码: {Code}", code);
                               LoginPage.LoginMessage = $"确认码: {code}";
                           },
                           message => Window.Dispatcher.Invoke(() => LoginPage.LoginMessage = message),
                           (text, caption, initialText) =>
                               Window.Dispatcher.Invoke(() => new DialogService(Window).ShowTextInput(text, caption, initialText, Window)),
                           challenge => Window.Dispatcher.Invoke
                           (() =>
                               {
                                   var dialog = new CaptchaInputWindow(challenge);

                                   if (Window.IsVisible)
                                   {
                                       dialog.Owner         = Window;
                                       dialog.ShowInTaskbar = false;
                                   }

                                   return dialog.ShowDialog() == true ? dialog.ResultText : null;
                               }
                           )
                       ),
                       loginCts.Token
                   ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MainWindow] 尝试登录至游戏失败");

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
                    return null;
                }

                if ((type == LoginType.QRCode || fallbackLoginType == LoginType.QRCode) && sdoLoginEx.Message == "二维码不存在或已过期，请重试")
                {
                    Log.Information("[MainWindow] 二维码已过期，等待手动刷新");
                    Window.Dispatcher.Invoke(() => LoginPage.IsQrCodeExpired = true);
                    LoginCardAfterCompletion = LoginCardType.ScanQRCode;
                    return null;
                }

                if (sdoLoginEx.ErrorCode is (int)LoginExceptionCode.CaptchaVerificationCanceled or (int)LoginExceptionCode.SafePhoneVerificationCanceled)
                {
                    Log.Information("[MainWindow] 用户主动取消登录验证流程: {ErrorCode}", sdoLoginEx.ErrorCode);
                    return null;
                }

                if (sdoLoginEx.RemoveAutoLoginSessionKey)
                {
                    Log.Information("[MainWindow] 快速登录失败, 清除 SessionKey: {Username}", username);
                    var account = AccountManager.Accounts.FirstOrDefault(x => x.UserName == username);

                    if (account != null)
                    {
                        account.SdoAutoLoginSessionKey = string.Empty;
                        AccountManager.Save(account);
                    }
                }

                msgbox = new CustomMessageBox.Builder()
                         .WithCaption("登录异常")
                         .WithImage(MessageBoxImage.Question)
                         .WithParentWindow(Window)
                         .WithText($"错误: {sdoLoginEx.Message}\n({sdoLoginEx.ErrorCode})");
                msgbox.Show();
                return null;
            }

            switch (ex)
            {
                case IOException:
                    msgbox
                        .WithText("搜寻游戏文件失败, 游戏路径可能设置错误");
                    break;

                case InvalidVersionFilesException:
                    msgbox.WithText("从游戏文件中读取版本信息失败, 可能需要重新安装或修复游戏文件");
                    break;

                case InvalidDataException invalidDataException when invalidDataException.Message.Contains("当前游戏数据版本", StringComparison.Ordinal):
                {
                    if (App.Settings.GamePath != null && Repository.Ffxiv.IsBaseVer(App.Settings.GamePath))
                    {
                        if (CustomMessageBox.Show
                            (
                                "未检测到游戏安装, 是否立即下载安装完整游戏?",
                                "XIVLauncherCN (Soil)",
                                MessageBoxButton.YesNo,
                                parentWindow: Window
                            )
                            == MessageBoxResult.Yes) await GameClientFileTaskService.RunAsync(GameClientFileTaskKind.FreshInstall).ConfigureAwait(false);

                        return null;
                    }

                    msgbox.WithText("无法确认当前游戏版本, 请先运行游戏文件修复后重试");
                    break;
                }

                case OAuthLoginException oauthLoginException:
                {
                    if ((type == LoginType.QRCode || fallbackLoginType == LoginType.QRCode) && oauthLoginException.OAuthErrorMessage == "二维码不存在或已过期，请重试")
                    {
                        Log.Information("[MainWindow] 二维码已过期，等待手动刷新");
                        Window.Dispatcher.Invoke(() => LoginPage.IsQrCodeExpired = true);
                        LoginCardAfterCompletion = LoginCardType.ScanQRCode;
                        return null;
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

                // Actual unexpected error; show error details
                default:
                    msgbox.WithShowNewGitHubIssue()
                          .WithAppendDescription(ex.ToString())
                          .WithAppendSettingsDescription("Login")
                          .WithAppendText("\n\n")
                          .WithAppendText("请检查登录信息, 并在稍后重试");
                    break;
            }

            msgbox.Show();
            return null;
        }
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

    private async Task<bool> ProcessLoginResultAsync(LoginResult loginResult, LoginAfterAction action)
    {
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

        if (loginResult.State == LoginState.NeedsPatchGame)
        {
            if (!ConfirmGamePatchInstall())
                return false;

            if (!await InstallGamePatchAsync().ConfigureAwait(false))
            {
                Log.Error("patchSuccess != true");
                return false;
            }

            loginResult.State = LoginState.Ok;
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
            List<Exception> exceptions = [];

            try
            {
                var oauthLogin = loginResult.OAuthLogin;

                if (oauthLogin == null || string.IsNullOrEmpty(oauthLogin.SessionID) || string.IsNullOrEmpty(oauthLogin.SndaID))
                {
                    Log.Error("[MainWindow] SID 或 SNDAID 为空，取消登录");
                    CustomMessageBox.Show
                    (
                        "登录异常: SID 或 SNDAID 为空",
                        "XIVLauncherCN (Soil)",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        showOfficialLauncher: true,
                        parentWindow: Window
                    );
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

    public async Task<FFXIVProcess?> StartGameAndAddon(LoginResult loginResult, bool forceNoDalamud, bool noThird, bool noPlugins)
    {
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

            return null;
        }

        var stopwatch = new Stopwatch();
        stopwatch.Start();
        var dalamudLauncher = DalamudLauncherFactory.Create
        (
            App.Settings.GamePath,
            App.Settings.DalamudLoadMethod,
            noPlugins,
            noThird
        );

        var dalamudOk = false;
        EnsureDalamudCompatibility();

        if (App.Settings.DalamudEnabled && !forceNoDalamud)
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
            LoginPage.Area.AreaID,
            LoginPage.Area.AreaLobby,
            LoginPage.Area.AreaGM,
            LoginPage.Area.AreaConfigUpload,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(loginResult.Areas))),
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

        AddonManager? addonMgr = null;

        try
        {
            addonMgr = GameLaunchService.StartAddons(launched.ProcessID);
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

            GameLaunchService.StopAddons(launched.ProcessID, addonMgr);
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
                                  options => StartGameAndAddon(loginResult, options.ForceNoDalamud, options.NoThirdPlugins, options.NoPlugins),
                                  LoginCancelSource?.Token ?? CancellationToken.None
                              )
                              .ConfigureAwait(false);
            }
            else
                await Task.Run(() => launched.UnderlyingProcess.WaitForExit()).ConfigureAwait(false);
        }
        finally
        {
            GameLaunchService.StopAddons(launched.ProcessID, addonMgr);
        }

        Log.Verbose("游戏进程已退出");

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
            App.DalamudUpdater.Run(true);
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

    private async Task<bool> InstallGamePatchAsync()
    {
        var result = await GameClientFileTaskService.RunAsync(GameClientFileTaskKind.Update).ConfigureAwait(false);
        return result.Status == GameClientFileTaskResultStatus.Success;
    }

    #endregion

    #region 杂项

    private void RefreshDalamudInfo() =>
        App.DalamudUpdater.Run(true);

    private void DalamudUpdaterStatusChanged(DalamudUpdater updater)
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
        var updater = App.DalamudUpdater;

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

    private static string GetDalamudLoadingText(DalamudUpdater updater)
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

    private static bool ShouldRequireNewAccountDeviceProfileSetup(XIVAccount? savedAccount) =>
        App.Settings.RequireDeviceProfileSetupForNewLogin
        && savedAccount == null;

    private void SavePendingNewAccountWithoutSecrets(XIVAccount account)
    {
        account.AutoLogin              = false;
        account.SdoAutoLoginSessionKey = string.Empty;
        account.WeGameTokenSecret      = null;
        account.SdoPassword            = string.Empty;
        account.WeGameSIDSecret        = null;
        account.WeGameSessionID        = string.Empty;
        account.GenerateID();
        AccountManager.AddAccount(account);
        AccountManager.CurrentAccount = account;
        AccountManager.Save();
    }

    private XIVAccount? FindSavedAccount(LoginType loginType, string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        if (loginType == LoginType.AutoLoginSession)
        {
            return AccountManager.Accounts.FirstOrDefault(account => string.Equals(account.SdoLoginAccount, username, StringComparison.Ordinal))
                   ?? AccountManager.Accounts.FirstOrDefault(account => string.Equals(account.UserName,     username, StringComparison.Ordinal));
        }

        return AccountManager.FindAccount(username, loginType.ToAccountType());
    }

    private static XIVAccountType ResolveAccountType(LoginType loginType, XIVAccount? savedAccount) =>
        loginType == LoginType.AutoLoginSession
            ? savedAccount?.AccountType ?? XIVAccountType.Sdo
            : loginType.ToAccountType();

    #endregion

    #region 事件

    private void OnPropertyChanged(string propertyName)
    {
        var handler = PropertyChanged;
        handler?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void OnWindowClosed(object? sender, object args)
    {
        App.DalamudUpdater.StatusChanged -= DalamudUpdaterStatusChanged;
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
        Logining   = 0,
        MainPage   = 1,
        ScanQRCode = 2,
        InjectMode = 3
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
