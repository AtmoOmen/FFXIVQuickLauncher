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
using XIVLauncher.Windows;
using XIVLauncher.Windows.Services;
using XIVLauncher.Xaml;
using Constants = XIVLauncher.Common.Constants;
using DCTravelClient = XIVLauncher.Common.Game.DCTravel.DCTravelClient;

namespace XIVLauncher.Windows.ViewModel;

public class MainWindowViewModel : INotifyPropertyChanged
{
    public const string                   PRESUDO_PASSWORD = "********假的密码********";
    public       SettingsControlViewModel Settings { get; }

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
    private CancellationTokenSource? AutoInjectDelayCancelSource { get; set; }
    private Task?                    ProcessRefreshTask         { get; set; }
    private bool                     IsInjecting                { get; set; }
    private bool                     IsLoginCanceledByUser      { get; set; }
    private LoginCardType?           LoginCardAfterCompletion   { get; set; }
    private int?                     PendingAutoInjectProcessId { get; set; }

    private HashSet<int> AutoInjectAttemptedProcessIds { get; } = [];

    public MainWindowViewModel(Window window)
    {
        Window   = window;
        Settings = new SettingsControlViewModel(new DialogService(window), new ExternalLaunchService());
        Settings.SettingsSaved += (_, _) => ReloadManualInjectSettings();

        StartLoginCommand       = new SyncCommand(CreateLoginHandler(LoginAfterAction.Start),               () => !IsLoggingIn);
        LoginNoStartCommand     = new SyncCommand(CreateLoginHandler(LoginAfterAction.UpdateOnly),          () => !IsLoggingIn);
        LoginNoDalamudCommand   = new SyncCommand(CreateLoginHandler(LoginAfterAction.StartWithoutDalamud), () => !IsLoggingIn);
        LoginNoPluginsCommand   = new SyncCommand(CreateLoginHandler(LoginAfterAction.StartWithoutPlugins), () => !IsLoggingIn);
        LoginNoThirdCommand     = new SyncCommand(CreateLoginHandler(LoginAfterAction.StartWithoutThird),   () => !IsLoggingIn);
        LoginRepairCommand      = new SyncCommand(CreateLoginHandler(LoginAfterAction.Repair),              () => !IsLoggingIn);
        LoginForceQRCommand     = new SyncCommand(CreateLoginHandler(LoginAfterAction.ForceQR),             () => !IsLoggingIn);
        RefreshQrCodeCommand    = new SyncCommand
        (
            _ =>
            {
                LoginCardAfterCompletion = LoginCardType.MainPage;
                StartLogin(LoginType.QRCode, Username, Password, IsFastLogin, IsReadWegameInfo, LoginAfterAction.Start);
            },
            () => !IsLoggingIn && IsQrCodeExpired
        );
        InjectModeSwitchCommand = new SyncCommand(_ => { SwitchMode(); },                                   () => !IsLoggingIn);
        InjectGameCommand       = new SyncCommand(_ => { StartInject(); },                                  () => !IsLoggingIn && SelectedProcess != null);
        FakeStartCommand        = new SyncCommand(_ => { FakeStartGame(); },                                () => !IsLoggingIn);
        LoginCancelCommand      = new SyncCommand(CreateLoginHandler(LoginAfterAction.CancelLogin));

        Launcher = new();

        var worldStatusBrushOk = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xf3));
        WorldStatusIconColor = worldStatusBrushOk;
        WorldStatusIconColor = new SolidColorBrush(Color.FromRgb(38, 38, 38));

        FFXIVProcesses.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAvailableProcesses));
            OnPropertyChanged(nameof(ProcessSelectionHint));
            CommandManager.InvalidateRequerySuggested();
        };

        ReloadManualInjectSettings();
    }

    #region 界面控制

    public void SwitchCard(LoginCardType i, bool shouldCancelLogin = true) =>
        Window.Dispatcher.Invoke
        (() =>
            {
                if (shouldCancelLogin)
                    CancelLogin();
                LoginCardTransitionerIndex = (int)i;

                ModeSwitchIcon  = i == LoginCardType.InjectMode ? PackIconKind.Login : PackIconKind.Injection;
                ModeSwitchTitle = i == LoginCardType.InjectMode ? "正常模式" : "手动注入模式";

                if (LoginCardTransitionerIndex == (int)LoginCardType.InjectMode)
                    StartRefreshFFXIVProcess();
                else
                    StopRefreshFFXIVProcess(true);
            }
        );

    public void SwitchMode() =>
        SwitchCard(LoginCardTransitionerIndex == (int)LoginCardType.InjectMode ? LoginCardType.MainPage : LoginCardType.InjectMode);

    private void ReloadManualInjectSettings()
    {
        AutoInjectEnabled   = App.Settings.ManualInjectAutoInjectEnabled.GetValueOrDefault(false);
        ManualInjectDelayMs = App.Settings.ManualInjectDelayMs;
    }

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

        IsLoggingIn           = true;
        IsEnabled             = false;
        IsLoginCanceledByUser = false;
        IsQrCodeExpired       = false;
        LoginCancelSource?.Dispose();
        LoginCancelSource     = new();
        CommandManager.InvalidateRequerySuggested();

        var currentCard = LoginCardAfterCompletion ?? (LoginCardType)LoginCardTransitionerIndex;
        LoginCardAfterCompletion = null;
        SwitchCard(loginType == LoginType.QRCode ? LoginCardType.ScanQRCode : LoginCardType.Logining, false);

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
                    var nextCard = LoginCardAfterCompletion ?? currentCard;
                    LoginCardAfterCompletion = null;
                    SwitchCard(nextCard, false);

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
            IsLoginCanceledByUser = true;

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
        if (IsLoginCanceledByUser)
            return;

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

        var finalLoginType             = loginType;
        var secret                     = string.Empty;
        var savedAccount               = FindSavedAccount(loginType, username);
        var accountType                = ResolveAccountType(loginType, savedAccount);
        var hasUnavailableSavedSecrets = AccountManager.HasUnavailableSecrets(savedAccount);
        XIVAccount? pendingNewAccount = null;

        try
        {
            inputPassword = inputPassword == PRESUDO_PASSWORD ? string.Empty : inputPassword?.Trim() ?? string.Empty;

            switch (loginType)
            {
                case LoginType.Static:
                    if (!inputPassword.IsNullOrEmpty())
                        secret = inputPassword;
                    else if (!hasUnavailableSavedSecrets && savedAccount?.Password is { Length: > 0 } savedPassword)
                        secret = await AccountManager.Decrypt(savedPassword);

                    ArgumentException.ThrowIfNullOrEmpty(username, "静态登录用户名");
                    ArgumentException.ThrowIfNullOrEmpty(secret,   "静态登录密码");
                    finalLoginType = LoginType.Static;
                    break;

                case LoginType.WeGameSID:
                    doingAutoLogin = true;
                    if (!readWeGameInfo && !hasUnavailableSavedSecrets && savedAccount?.TestSID is { Length: > 0 } testSid)
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
                    savedAccount   = FindSavedAccount(finalLoginType, username);
                    accountType    = ResolveAccountType(finalLoginType, savedAccount);
                    break;

                case LoginType.WeGameToken:
                    if (!hasUnavailableSavedSecrets && inputPassword.IsNullOrEmpty() && savedAccount?.AutoLoginSessionKey is { Length: > 0 } autoLoginSessionKey)
                    {
                        secret         = await AccountManager.CredProvider.Decrypt(autoLoginSessionKey);
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
                    if (!hasUnavailableSavedSecrets && doingAutoLogin && savedAccount?.AutoLoginSessionKey is { Length: > 0 } slideAutoLoginSessionKey)
                    {
                        secret         = await AccountManager.Decrypt(slideAutoLoginSessionKey);
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

        var requiresNewAccountDeviceProfileSetup = ShouldRequireNewAccountDeviceProfileSetup(savedAccount, action);
        var shouldRequestTemporaryAutoLoginSession = requiresNewAccountDeviceProfileSetup && loginType == LoginType.QRCode;
        var loginAutoLogin = doingAutoLogin || shouldRequestTemporaryAutoLoginSession;

        ResolvedDeviceProfile resolvedDeviceProfile;

        if (requiresNewAccountDeviceProfileSetup && loginType != LoginType.QRCode)
        {
            pendingNewAccount = CreatePendingNewAccount(username, username, accountType);

            switch (PromptNewAccountDeviceProfileChoice())
            {
                case MessageBoxResult.Yes:
                    resolvedDeviceProfile = AccountManager.ResolveDeviceProfile(pendingNewAccount);
                    break;

                case MessageBoxResult.No:
                {
                    var configuredNewAccount = CreateIndependentDeviceProfileDraft(pendingNewAccount);
                    if (!ShowTemporaryAccountDeviceProfileSettings(configuredNewAccount))
                    {
                        SavePendingNewAccountWithoutSecrets(pendingNewAccount);
                        return;
                    }

                    pendingNewAccount      = configuredNewAccount;
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
                App.AccountManager.CurrentAccount.AreaName = name;
                App.AccountManager.Save();
            }
        };

        var attemptedV3AutoUpdate = false;
        LoginResult loginResult;

        while (true)
        {
            var nextLoginResult = await LoginToGameAsync(finalLoginType, loginType, username, secret, loginAutoLogin, deviceProfileSnapshot, dcTraveler, action).ConfigureAwait(false);
            if (nextLoginResult == null)
                return;

            loginResult = nextLoginResult;
            loginResult.Area  = Area;
            loginResult.Areas = LoginAreas;

            if (!attemptedV3AutoUpdate
                && action is not (LoginAfterAction.UpdateOnly or LoginAfterAction.Repair)
                && loginResult.State == LoginState.NeedsPatchGame
                && loginResult.V3GameUpdatePlan != null)
            {
                if (!ConfirmGamePatchInstall())
                    return;

                if (!await InstallGamePatch(loginResult).ConfigureAwait(false))
                    return;

                attemptedV3AutoUpdate = true;
                continue;
            }

            break;
        }

        var oAuthLogin = loginResult.OAuthLogin;

        if (requiresNewAccountDeviceProfileSetup && loginType == LoginType.QRCode && loginResult.State == LoginState.Ok && oAuthLogin != null)
        {
            pendingNewAccount ??= CreatePendingNewAccount(oAuthLogin.InputUserID, oAuthLogin.SndaID, accountType);

            switch (PromptNewAccountDeviceProfileChoice())
            {
                case MessageBoxResult.Yes:
                    resolvedDeviceProfile = AccountManager.ResolveDeviceProfile(pendingNewAccount);
                    deviceProfileSnapshot = resolvedDeviceProfile.Snapshot;
                    break;

                case MessageBoxResult.No:
                {
                    var configuredNewAccount = CreateIndependentDeviceProfileDraft(pendingNewAccount);
                    if (!ShowTemporaryAccountDeviceProfileSettings(configuredNewAccount))
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

                    pendingNewAccount      = configuredNewAccount;
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
                                            dcTraveler,
                                            action
                                        ).ConfigureAwait(false);
                    if (reloginResult == null)
                        return;

                    loginResult       = reloginResult;
                    loginResult.Area  = Area;
                    loginResult.Areas = LoginAreas;
                    oAuthLogin        = loginResult.OAuthLogin;
                    break;
                }

                default:
                    return;
            }
        }

        if (loginResult.State == LoginState.NeedsPatchGame && action != LoginAfterAction.Repair)
            action = LoginAfterAction.UpdateOnly;

        if (action != LoginAfterAction.UpdateOnly)
        {
            if (loginResult.State == LoginState.Ok)
            {
                var deviceProfileAccount = pendingNewAccount ?? savedAccount;

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
                    AutoLogin                          = loginType == LoginType.WeGameSID || doingAutoLogin,
                    LoginAccount                       = oAuthLogin?.InputUserID!,
                    SndaId                             = oAuthLogin?.SndaID!,
                    AccountType                        = accountType,
                    AreaName                           = Area.AreaName,
                    UserDefinedName                    = deviceProfileAccount?.UserDefinedName            ?? null!,
                    DeviceProfilePresetId              = deviceProfileAccount?.DeviceProfilePresetId      ?? string.Empty,
                    DeviceProfileDynamicEnabled        = deviceProfileAccount?.DeviceProfileDynamicEnabled ?? false,
                    IsDeviceProfileRotation            = deviceProfileAccount?.IsDeviceProfileRotation    ?? true,
                    DeviceProfileRotationDays          = deviceProfileAccount?.DeviceProfileRotationDays  ?? AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS,
                    DeviceProfileLastGeneratedUtcTicks = deviceProfileAccount?.DeviceProfileLastGeneratedUtcTicks ?? 0
                };

                AccountManager.ApplyResolvedDeviceProfile(accountToSave, resolvedDeviceProfile);

                if (doingAutoLogin && accountToSave.AccountType != XIVAccountType.WeGameSID)
                {
                    if (!string.IsNullOrEmpty(oAuthLogin?.AutoLoginSessionKey))
                        accountToSave.AutoLoginSessionKey = await AccountManager.Encrypt(oAuthLogin.AutoLoginSessionKey);

                    if (DCTravelListener != null && !string.IsNullOrEmpty(oAuthLogin?.AutoLoginSessionKey))
                    {
                        DCTravelListener.DCTravelClient.RefreshGameSessionIDByAutoLoginFunc = async () =>
                        {
                            var newLoginResult = await Launcher.LoginClient.LoginBySessionKey
                                                 (
                                                     username,
                                                     oAuthLogin.AutoLoginSessionKey,
                                                     DCTravelListener.DCTravelClient,
                                                     deviceProfileSnapshot
                                                 ).ConfigureAwait(false);
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

    private async Task<LoginResult?> LoginToGameAsync
    (
        LoginType             type,
        LoginType             fallbackLoginType,
        string                username,
        string                secret,
        bool                  autoLogin,
        DeviceProfileSnapshot deviceProfile,
        DCTravelClient        dcTravelClient,
        LoginAfterAction      action
    )
    {
        if (IsLoginCanceledByUser)
            return null;

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
            LoginCancelSource?.Dispose();
            LoginCancelSource = new();
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
                                       QRCodeBitmapImage = qrBytes.ToBitmapImage();
                                       IsQrCodeExpired  = false;
                                   }
                               );
                           },
                           code =>
                           {
                               if (requestLoginType != LoginType.Slide)
                                   return;

                               Log.Information("[MainWindow] 接收到叨鱼确认码: {Code}", code);
                               LoginMessage = $"确认码: {code}";
                           },
                           message => Window.Dispatcher.Invoke(() => LoginMessage = message),
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
                    Window.Dispatcher.Invoke(() => IsQrCodeExpired = true);
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
                        account.AutoLoginSessionKey = string.Empty;
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

            var disableAutoLogin = false;

            switch (ex)
            {
                case IOException:
                    msgbox
                        .WithText("搜寻游戏文件失败, 游戏路径可能设置错误");
                    break;

                case InvalidVersionFilesException:
                    msgbox.WithText("从游戏文件中读取版本信息失败, 可能需要重新安装或修复游戏文件");
                    break;

                case OAuthLoginException oauthLoginException:
                {
                    if ((type == LoginType.QRCode || fallbackLoginType == LoginType.QRCode) && oauthLoginException.OAuthErrorMessage == "二维码不存在或已过期，请重试")
                    {
                        Log.Information("[MainWindow] 二维码已过期，等待手动刷新");
                        Window.Dispatcher.Invoke(() => IsQrCodeExpired = true);
                        LoginCardAfterCompletion = LoginCardType.ScanQRCode;
                        return null;
                    }

                    disableAutoLogin = true;
                    LoginMessage     = string.Empty;
                    QRDialog.CloseQRWindow(Window);

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
                    disableAutoLogin = true;
                    msgbox.WithShowNewGitHubIssue()
                          .WithAppendDescription(ex.ToString())
                          .WithAppendSettingsDescription("Login")
                          .WithAppendText("\n\n")
                          .WithAppendText("请检查登录信息, 并在稍后重试");
                    break;
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

    private bool ConfirmGamePatchInstall()
    {
        if (!(App.Settings.AskBeforePatchInstall ?? true))
            return true;

        var selfPatchAsk = CustomMessageBox.Show
        (
            "需要更新游戏才能继续游玩\n是否要下载更新文件并安装?",
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
                        "游戏服务器返回错误响应, 无法修复游戏",
                        "XIVLauncherCN (Soil)",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        parentWindow: Window
                    );

                    return false;
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.Builder.NewFrom(ex, "ProcessLoginResultAsync/Repair").WithParentWindow(Window).Show();

                return false;
            }
        }

        if (loginResult.State == LoginState.NeedsPatchGame)
        {
            if (!ConfirmGamePatchInstall())
                return false;

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
                "更新检查已完成, 无剩余待安装更新内容",
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
            List<Exception> exceptions = [];

            try
            {
                var oauthLogin = loginResult.OAuthLogin;

                if (oauthLogin == null || oauthLogin.SessionID.IsNullOrEmpty() || oauthLogin.SndaID.IsNullOrEmpty())
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
                if (process.ExitCode is not (0 or 0x12345678) && (App.Settings.TreatNonZeroExitCodeAsFailure ?? false))
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

    #region 注入

    public void StartInject() =>
        StartInject(SelectedProcess, false);

    private void StartInject(FFXIVProcess? targetProcess, bool isAutoInjection)
    {
        if (Window.Dispatcher != Dispatcher.CurrentDispatcher)
        {
            Window.Dispatcher.Invoke(() => StartInject(targetProcess, isAutoInjection));
            return;
        }

        if (IsInjecting || targetProcess == null)
            return;

        CancelPendingAutoInject();

        if (!isAutoInjection)
        {
            IsLoadingDialogOpen  = true;
            LoadingDialogMessage = "注入中...";
        }

        IsInjecting = true;
        CommandManager.InvalidateRequerySuggested();

        Task.Run
        (() =>
            {
                try
                {
                    var gamePid = targetProcess.ProcessID;

                    if (targetProcess.HasInjected)
                    {
                        if (isAutoInjection)
                            return;

                        CustomMessageBox.Builder
                                        .NewFrom("选定进程已被注入")
                                        .WithButtons(MessageBoxButton.OK)
                                        .WithCaption("XIVLauncherCN (Soil)")
                                        .WithParentWindow(Window)
                                        .Show();
                        return;
                    }

                    if (!InjectGameAndAddon(gamePid))
                        return;

                    Window.Dispatcher.Invoke(() => { targetProcess.HasInjected = true; });

                    if (isAutoInjection)
                        return;

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
                catch (Exception ex)
                {
                    CustomMessageBox.Builder
                                    .NewFromUnexpectedException(ex, "InjectGame")
                                    .WithParentWindow(Window)
                                    .Show();
                }
                finally
                {
                    Window.Dispatcher.Invoke
                    (() =>
                        {
                            IsLoadingDialogOpen = false;
                            IsInjecting         = false;
                            CommandManager.InvalidateRequerySuggested();
                            SyncAutoInjectState();

                            if (!isAutoInjection)
                                Activate();
                        }
                    );
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

    private void CancelPendingAutoInject()
    {
        if (AutoInjectDelayCancelSource == null)
            return;

        AutoInjectDelayCancelSource.Cancel();
        AutoInjectDelayCancelSource.Dispose();
        AutoInjectDelayCancelSource = null;
        PendingAutoInjectProcessId  = null;
    }

    private void CleanupAutoInjectAttemptedProcesses()
    {
        var activeProcessIds = FFXIVProcesses.Select(process => process.ProcessID).ToHashSet();
        AutoInjectAttemptedProcessIds.RemoveWhere(processId => !activeProcessIds.Contains(processId));
    }

    private bool CanAutoInject() =>
        LoginCardTransitionerIndex == (int)LoginCardType.InjectMode
        && AutoInjectEnabled
        && !IsLoggingIn
        && !IsInjecting;

    private void SyncAutoInjectState()
    {
        if (!CanAutoInject())
        {
            CancelPendingAutoInject();
            return;
        }

        var candidate = FFXIVProcesses.FirstOrDefault(process => !process.HasInjected && !AutoInjectAttemptedProcessIds.Contains(process.ProcessID));

        if (candidate == null)
        {
            CancelPendingAutoInject();
            return;
        }

        if (PendingAutoInjectProcessId == candidate.ProcessID)
            return;

        CancelPendingAutoInject();
        PendingAutoInjectProcessId  = candidate.ProcessID;
        AutoInjectDelayCancelSource = new CancellationTokenSource();

        var autoInjectToken = AutoInjectDelayCancelSource.Token;
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

                    Window.Dispatcher.Invoke
                    (() =>
                        {
                            if (PendingAutoInjectProcessId != candidate.ProcessID || !CanAutoInject())
                                return;

                            var process = FFXIVProcesses.FirstOrDefault(p => p.ProcessID == candidate.ProcessID);
                            if (process == null || process.HasInjected)
                                return;

                            AutoInjectAttemptedProcessIds.Add(process.ProcessID);
                            SelectedProcess = process;
                            StartInject(process, true);
                        }
                    );
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
                finally
                {
                    if (PendingAutoInjectProcessId == candidate.ProcessID)
                        PendingAutoInjectProcessId = null;
                }
            },
            autoInjectToken
        );
    }

    public void StartRefreshFFXIVProcess()
    {
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
                    // ignored
                }
            },
            processRefreshToken
        );
    }

    private void StopRefreshFFXIVProcess(bool clearCollection)
    {
        CancelPendingAutoInject();

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

        AutoInjectAttemptedProcessIds.Clear();
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
                              new RestartMonitor.RestartOptions(forceNoDalamud, noThird, noPlugins),
                              options => StartGameAndAddon(loginResult, options.ForceNoDalamud, options.NoThirdPlugins, options.NoPlugins),
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

    private Task<bool> RepairGame(LoginResult loginResult) =>
        HandleV3GamePatchAsync(loginResult, PatchVerifierMode.Repair);

    private async Task<bool> HandleV3GamePatchAsync(LoginResult loginResult, PatchVerifierMode mode)
    {
        var doLogin = false;
        using var mutex = new Mutex(false, "XivLauncherIsPatching");

        if (!mutex.WaitOne(0, false))
        {
            CustomMessageBox.Show
            (
                "XIVLauncher 正在另一进程中执行游戏更新, 请检查是否开启了多个 XIVLauncher 实例",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: Window
            );

            return false;
        }

        var actionText = mode == PatchVerifierMode.Update ? "更新" : "修复";
        Log.Information("[MainWindow] 开始{Action}游戏", actionText);

        if (!AppUtil.TryYellOnGameFilesBeingOpen(Window, _ => $"关闭以下进程以{actionText}游戏"))
            return false;

        using var verify = new PatchVerifier(CommonSettings.Instance, mode, TimeSpan.FromMilliseconds(100));

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
                    if (mode == PatchVerifierMode.Update)
                    {
                        doLogin  = true;
                        doVerify = false;
                        break;
                    }

                    var windowResult =
                        CustomMessageBox.Builder
                                        .NewFrom
                                        (
                                            verify.NumBrokenFiles switch
                                            {
                                                0 => "未检测到任何损坏的游戏文件",
                                                _ => $"已成功修复 {verify.NumBrokenFiles} 个游戏文件"
                                            }
                                        )
                                        .WithAppendText
                                        (
                                            verify.MovedFiles.Count switch
                                            {
                                                0 => string.Empty,
                                                _ => $"\n已将对应的 {verify.MovedFiles.Count} 个非原始游戏文件移动至 {verify.MovedFileToDir}"
                                            }
                                        )
                                        .WithDescription(verify.MovedFiles.Count != 0 ? string.Join("\n", verify.MovedFiles.Select(x => $"* {x}")) : string.Empty)
                                        .WithImage(MessageBoxImage.Information)
                                        .WithButtons(MessageBoxButton.YesNoCancel)
                                        .WithYesButtonText("启动游戏")
                                        .WithNoButtonText("再次验证")
                                        .WithCancelButtonText("关闭")
                                        .WithParentWindow(Window)
                                        .Show();

                    switch (windowResult)
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
                    doLogin  = false;
                    doVerify = ShowPatchVerifierRetryDialog(verify, mode);
                    break;

                case PatchVerifier.VerifyState.Cancelled:
                    doLogin = doVerify = false;
                    break;
            }
        }

        progressDialog.Dispatcher.Invoke(progressDialog.Close);
        return doLogin;
    }

    private bool ShowPatchVerifierRetryDialog(PatchVerifier verify, PatchVerifierMode mode)
    {
        var actionText = mode == PatchVerifierMode.Update ? "更新" : "修复";

        if (verify.LastException != null && verify.LastException.ToString().Contains("Data error"))
        {
            return new CustomMessageBox.Builder()
                   .WithText($"{actionText}失败: 检查游戏文件过程中硬盘报错，可能表明其存在物理故障\n请检查硬盘健康状态, 或尝试更新硬盘固件\n将游戏重新安装至其他路径, 或可暂时解决该问题")
                   .WithExitOnClose(CustomMessageBox.ExitOnCloseModes.DontExitOnClose)
                   .WithImage(MessageBoxImage.Error)
                   .WithShowHelpLinks()
                   .WithShowDiscordLink()
                   .WithShowNewGitHubIssue(false)
                   .WithButtons(MessageBoxButton.OKCancel)
                   .WithOkButtonText("重试")
                   .WithParentWindow(Window)
                   .Show()
                   == MessageBoxResult.OK;
        }

        if (verify.LastException == null)
        {
            return new CustomMessageBox.Builder()
                   .WithText($"{actionText}失败: 未知错误, 可能需要重新安装游戏")
                   .WithImage(MessageBoxImage.Exclamation)
                   .WithButtons(MessageBoxButton.OKCancel)
                   .WithOkButtonText("重试")
                   .WithParentWindow(Window)
                   .Show()
                   == MessageBoxResult.OK;
        }

        return CustomMessageBox.Builder
                               .NewFrom(verify.LastException, "PatchVerifier")
                               .WithAppendText("\n\n")
                               .WithAppendText($"{actionText}失败: 可能需要重新安装游戏")
                               .WithImage(MessageBoxImage.Exclamation)
                               .WithButtons(MessageBoxButton.OKCancel)
                               .WithOkButtonText("重试")
                               .WithParentWindow(Window)
                               .Show()
               == MessageBoxResult.OK;
    }

    private Task<bool> InstallGamePatch(LoginResult loginResult)
    {
        if (loginResult.V3GameUpdatePlan != null)
            return HandleV3GamePatchAsync(loginResult, PatchVerifierMode.Update);

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
                                 ContinueWhenGameOpen =
                                     () => CustomMessageBox.Builder
                                                           .NewFrom("官方启动器或游戏正在运行, 无法进行游戏更新\n请全部关闭后重试")
                                                           .WithImage(MessageBoxImage.Exclamation)
                                                           .WithButtons(MessageBoxButton.OKCancel)
                                                           .WithOkButtonText("刷新")
                                                           .WithDefaultResult(MessageBoxResult.OK)
                                                           .Show()
                                           != MessageBoxResult.Cancel,
                                 EnsureGameFilesClosed = () => AppUtil.TryYellOnGameFilesBeingOpen
                                 (
                                     Window,
                                     _ => "关闭以下进程以更新游戏"
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
                        "XIVLauncher 正在另一进程中执行游戏更新, 请检查是否开启了多个 XIVLauncher 实例",
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
                        $"错误: 无法正确启动补丁安装程序\n{result.Exception?.Message}",
                        "XIVLauncherCN (Soil)",
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
                            case NotEnoughSpaceException.SpaceKind.AllPatches:
                                CustomMessageBox.Show
                                (
                                    $"磁盘空间不足, 无法安装更新文件\n可在设置中更改下载位置\n\n需要: {bytesRequired}\n可用: {bytesFree}",
                                    "XIVLauncherCN (Soil)",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error,
                                    parentWindow: Window
                                );
                                break;

                            case NotEnoughSpaceException.SpaceKind.Game:
                                CustomMessageBox.Show
                                (
                                    $"磁盘空间不足, 无法安装更新文件\n\n可在设置中更改游戏安装位置\n\n需要: {bytesRequired}\n可用: {bytesFree}",
                                    "XIVLauncherCN (Soil)",
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
                    {
                        CustomMessageBox.Show
                        (
                            "安装更新文件失败: 未知错误",
                            "XIVLauncherCN (Soil)",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error,
                            parentWindow: Window
                        );
                    }
                    else
                    {
                        CustomMessageBox.Builder
                                        .NewFromUnexpectedException(result.Exception, "HandlePatchAsync")
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
                                          .NewFrom("XIVLauncher 将会搜寻任何与原版不一致的游戏文件并替换修复\n这可能导致使用 TexTools 安装的模组被还原\n持续时间可能较长, 请确认是否要继续?")
                                          .WithButtons(MessageBoxButton.YesNo)
                                          .WithImage(MessageBoxImage.Question)
                                          .WithParentWindow(Window)
                                          .Show();

                if (res == MessageBoxResult.No)
                    return;
            }

            StartLogin(LoginTypeOption.LoginType, Username, Password, IsFastLogin, IsReadWegameInfo, action);
        };

    private bool ShouldRequireNewAccountDeviceProfileSetup(XIVAccount? savedAccount, LoginAfterAction action) =>
        App.Settings.RequireDeviceProfileSetupForNewAccountLogin.GetValueOrDefault(false)
        && savedAccount == null
        && action != LoginAfterAction.UpdateOnly;

    private XIVAccount CreatePendingNewAccount(string loginAccount, string sndaId, XIVAccountType accountType) =>
        new()
        {
            LoginAccount                = loginAccount,
            SndaId                      = sndaId,
            AccountType                 = accountType,
            AreaName                    = Area?.AreaName ?? string.Empty,
            DeviceProfilePresetId       = string.Empty,
            DeviceProfileDynamicEnabled = false,
            IsDeviceProfileRotation     = true,
            DeviceProfileRotationDays   = AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS
        };

    private XIVAccount CreateIndependentDeviceProfileDraft(XIVAccount account) =>
        new()
        {
            LoginAccount                       = account.LoginAccount,
            SndaId                             = account.SndaId,
            AccountType                        = account.AccountType,
            AreaName                           = account.AreaName,
            UserDefinedName                    = account.UserDefinedName,
            DeviceProfilePresetId              = account.DeviceProfilePresetId,
            DeviceProfileDynamicEnabled        = true,
            IsDeviceProfileRotation            = account.IsDeviceProfileRotation,
            DeviceProfileRotationDays          = account.DeviceProfileRotationDays,
            DeviceProfileLastGeneratedUtcTicks = account.DeviceProfileLastGeneratedUtcTicks
        };

    private void SavePendingNewAccountWithoutSecrets(XIVAccount account)
    {
        account.AutoLogin           = false;
        account.AutoLoginSessionKey = string.Empty;
        account.Password            = string.Empty;
        account.TestSID             = null;
        account.NSessionId          = string.Empty;
        account.GenerateID();
        AccountManager.AddAccount(account);
        AccountManager.CurrentAccount = account;
        AccountManager.Save();
    }

    private MessageBoxResult PromptNewAccountDeviceProfileChoice() =>
        CustomMessageBox.Builder
                        .NewFrom("检测到新账号首次登录，需先确认本次使用的设备信息")
                        .WithCaption("设备信息")
                        .WithButtons(MessageBoxButton.YesNo)
                        .WithYesButtonText("使用共享设备信息")
                        .WithNoButtonText("配置账号设备信息")
                        .WithDefaultResult(MessageBoxResult.Yes)
                        .WithImage(MessageBoxImage.Question)
                        .WithParentWindow(Window)
                        .Show();

    private bool ShowTemporaryAccountDeviceProfileSettings(XIVAccount account) =>
        Window.Dispatcher.Invoke
        (() =>
            {
                var dialog = new AccountDeviceProfileSettingsWindow(account, AccountManager, true);

                if (Window.IsVisible)
                {
                    dialog.Owner         = Window;
                    dialog.ShowInTaskbar = false;
                }

                return dialog.ShowDialog() == true;
            }
        );

    private XIVAccount? FindSavedAccount(LoginType loginType, string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        if (loginType == LoginType.AutoLoginSession)
        {
            return AccountManager.Accounts.FirstOrDefault(account => string.Equals(account.LoginAccount, username, StringComparison.Ordinal))
                   ?? AccountManager.Accounts.FirstOrDefault(account => string.Equals(account.UserName,  username, StringComparison.Ordinal));
        }

        return AccountManager.FindAccount(username, loginType.ToAccountType());
    }

    private static XIVAccountType ResolveAccountType(LoginType loginType, XIVAccount? savedAccount) =>
        loginType == LoginType.AutoLoginSession
            ? savedAccount?.AccountType ?? XIVAccountType.Sdo
            : loginType.ToAccountType();

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

    public void OnWindowClosed(object? sender, object args)
    {
        StopRefreshFFXIVProcess(true);
        CancelLogin();
        Application.Current.Shutdown();
    }

    public void OnWindowClosing(object? sender, CancelEventArgs args)
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
    public ICommand RefreshQrCodeCommand    { get; set; }
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

    public bool IsQrCodeExpired
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(IsQrCodeExpired));
            CommandManager.InvalidateRequerySuggested();
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
    } = PackIconKind.Injection;

    public string ModeSwitchTitle
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(ModeSwitchTitle));
        }
    } = "手动注入模式";

    public bool AutoInjectEnabled
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            App.Settings.ManualInjectAutoInjectEnabled = value;

            if (!value)
            {
                CancelPendingAutoInject();
                AutoInjectAttemptedProcessIds.Clear();
            }

            OnPropertyChanged(nameof(AutoInjectEnabled));
            SyncAutoInjectState();
        }
    }

    public decimal? ManualInjectDelayMs
    {
        get;
        set
        {
            if (field == value)
                return;

            field                       = value;
            App.Settings.ManualInjectDelayMs = value ?? 0;
            Settings.ManualInjectDelayMs = value;
            OnPropertyChanged(nameof(ManualInjectDelayMs));
            SyncAutoInjectState();
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
