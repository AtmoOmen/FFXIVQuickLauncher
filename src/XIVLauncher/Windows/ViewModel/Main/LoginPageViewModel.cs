using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using XIVLauncher.Common.Game;
using XIVLauncher.Login.Models;
using XIVLauncher.Windows.GameClientFiles;

namespace XIVLauncher.Windows.ViewModel.Main;

public sealed partial class LoginPageViewModel : ObservableObject
{
    private readonly Func<bool>                                   isBusyFunc;
    private readonly Action<LoginPageViewModel, LoginAfterAction> requestLoginAction;
    private readonly Func<GameClientFileTaskKind, Task>           requestGameClientFileTaskAction;
    private readonly Action                                       requestCancelLoginAction;
    private readonly Action<LoginPageViewModel>                   requestRefreshQrCodeAction;
    private readonly Action                                       requestShowInjectPageAction;
    private readonly Action                                       requestBackToMainPageAction;
    private readonly Action                                       requestFakeStartAction;

    public LoginPageViewModel
    (
        Func<bool>                                   isBusyFunc,
        Action<LoginPageViewModel, LoginAfterAction> requestLoginAction,
        Func<GameClientFileTaskKind, Task>           requestGameClientFileTaskAction,
        Action                                       requestCancelLoginAction,
        Action<LoginPageViewModel>                   requestRefreshQrCodeAction,
        Action                                       requestShowInjectPageAction,
        Action                                       requestBackToMainPageAction,
        Action                                       requestFakeStartAction
    )
    {
        this.isBusyFunc                      = isBusyFunc;
        this.requestLoginAction              = requestLoginAction;
        this.requestGameClientFileTaskAction = requestGameClientFileTaskAction;
        this.requestCancelLoginAction        = requestCancelLoginAction;
        this.requestRefreshQrCodeAction      = requestRefreshQrCodeAction;
        this.requestShowInjectPageAction     = requestShowInjectPageAction;
        this.requestBackToMainPageAction     = requestBackToMainPageAction;
        this.requestFakeStartAction          = requestFakeStartAction;

        LoginTypeOptions = [.. LoginTypeOption.Get()];

        loginTypeOption = LoginTypeOptions.FirstOrDefault(x => x.LoginType == App.Settings.SelectedLoginType) ??
                          LoginTypeOptions.First(x => x.LoginType          == LoginType.Slide);

        ApplyLoginType(loginTypeOption.LoginType);
    }

    public LoginTypeOption[] LoginTypeOptions { get; }

    [ObservableProperty]
    public partial bool IsFastLogin { get; set; }

    [ObservableProperty]
    public partial bool IsFastLoginEnabled { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsReadWegameInfo { get; set; }

    partial void OnIsReadWegameInfoChanged
    (
        bool value
    )
    {
        if (isApplyingLoginType)
            return;

        RefreshStartLoginState();
    }

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    partial void OnUsernameChanged
    (
        string value
    )
    {
        RefreshAccountStatus();

        if (!isApplyingLoginType)
            RefreshStartLoginState();
    }

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    partial void OnPasswordChanged
    (
        string value
    )
    {
        if (!isApplyingLoginType)
            RefreshStartLoginState();
    }

    public LoginTypeOption LoginTypeOption
    {
        get => loginTypeOption;
        set
        {
            var previousGroup = loginTypeOption?.Group;

            if (!SetProperty(ref loginTypeOption!, value))
                return;

            App.Settings.SelectedLoginType = value.LoginType;
            if (previousGroup.HasValue && previousGroup.Value != value.Group)
                Username = string.Empty;
            Password = string.Empty;
            ApplyLoginType(value.LoginType);
        }
    }

    public int AreaIndex
    {
        set => App.Settings.SelectedServer = value;
    }

    [ObservableProperty]
    public partial LoginArea? Area { get; set; }

    partial void OnAreaChanged
    (
        LoginArea? oldValue,
        LoginArea? newValue
    ) =>
        Log.Information("大区变更 {OldArea} -> {NewArea}", oldValue, newValue);

    [ObservableProperty]
    public partial LoginArea[] LoginAreas { get; set; } = [];

    [ObservableProperty]
    public partial string LoginMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial BitmapImage? QRCodeBitmapImage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshQrCodeCommand))]
    public partial bool IsQrCodeExpired { get; set; }

    public bool CanStartLogin => !isBusyFunc() && IsLoginInputComplete;

    [ObservableProperty]
    public partial bool IsUsernameVisible { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsUsernameEnabled { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsPasswordVisible { get; private set; }

    [ObservableProperty]
    public partial bool IsFastLoginVisible { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsReadWegameInfoVisible { get; private set; }

    [ObservableProperty]
    public partial string FastLoginText { get; private set; } = "记住账号";

    [ObservableProperty]
    public partial bool IsNewAccountBadgeVisible { get; private set; }

    [ObservableProperty]
    public partial string UsernameHint { get; private set; } = "盛趣账号";

    [ObservableProperty]
    public partial string UsernameToolTip { get; private set; } = "输入盛趣账号";

    [ObservableProperty]
    public partial string PasswordHint { get; private set; } = "密码";

    [ObservableProperty]
    public partial string ReadWeGameInfoText { get; private set; } = "重新获取账号信息";

    [ObservableProperty]
    public partial string ReadWeGameInfoToolTip { get; private set; } = "勾选后启动 WeGame 并读取当前启动账号的 SndaID 和 SID";

    public void SelectLoginType
    (
        LoginType loginType
    )
    {
        var option = LoginTypeOptions.FirstOrDefault(x => x.LoginType == loginType);

        if (option != null)
            LoginTypeOption = option;
    }

    public void RefreshCommandStates()
    {
        StartLoginCommand.NotifyCanExecuteChanged();
        LoginNoDalamudCommand.NotifyCanExecuteChanged();
        LoginNoPluginsCommand.NotifyCanExecuteChanged();
        LoginNoThirdCommand.NotifyCanExecuteChanged();
        LoginRepairCommand.NotifyCanExecuteChanged();
        RunIntegrityCheckCommand.NotifyCanExecuteChanged();
        LoginCancelCommand.NotifyCanExecuteChanged();
        LoginForceQRCommand.NotifyCanExecuteChanged();
        RefreshQrCodeCommand.NotifyCanExecuteChanged();
        InjectModeSwitchCommand.NotifyCanExecuteChanged();
        BackToMainPageCommand.NotifyCanExecuteChanged();
        FakeStartCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStartLogin));
    }

    private LoginTypeOption loginTypeOption = null!;

    private bool isApplyingLoginType;

    private bool IsLoginInputComplete => loginTypeOption.LoginType switch
    {
        LoginType.QRCode => true,
        LoginType.WeGame => !string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(Password) || IsReadWegameInfo || IsFastLogin,
        _                => !string.IsNullOrWhiteSpace(Username) && (!IsPasswordVisible || !string.IsNullOrWhiteSpace(Password))
    };

    [RelayCommand(CanExecute = nameof(CanStartLoginExecute))]
    private void StartLogin() =>
        requestLoginAction(this, LoginAfterAction.Start);

    private bool CanStartLoginExecute() => CanStartLogin;

    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotBusy))]
    private void LoginNoDalamud() =>
        requestLoginAction(this, LoginAfterAction.StartWithoutDalamud);

    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotBusy))]
    private void LoginNoPlugins() =>
        requestLoginAction(this, LoginAfterAction.StartWithoutPlugins);

    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotBusy))]
    private void LoginNoThird() =>
        requestLoginAction(this, LoginAfterAction.StartWithoutThird);

    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotBusy))]
    private Task LoginRepair() =>
        requestGameClientFileTaskAction(GameClientFileTaskKind.Repair);

    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotBusy))]
    private Task RunIntegrityCheck() =>
        requestGameClientFileTaskAction(GameClientFileTaskKind.IntegrityCheck);

    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotBusy))]
    private void LoginForceQR() =>
        requestLoginAction(this, LoginAfterAction.ForceQR);

    [RelayCommand]
    private void LoginCancel() =>
        requestCancelLoginAction();

    [RelayCommand(CanExecute = nameof(CanRefreshQrCode))]
    private void RefreshQrCode() =>
        requestRefreshQrCodeAction(this);

    private bool CanRefreshQrCode() =>
        !isBusyFunc() && IsQrCodeExpired;

    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotBusy))]
    private void InjectModeSwitch() =>
        requestShowInjectPageAction();

    [RelayCommand]
    private void BackToMainPage() =>
        requestBackToMainPageAction();

    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotBusy))]
    private void FakeStart() =>
        requestFakeStartAction();

    private bool CanExecuteWhenNotBusy() =>
        !isBusyFunc();

    private void ApplyLoginType
    (
        LoginType loginType
    )
    {
        isApplyingLoginType = true;

        try
        {
            IsUsernameVisible       = true;
            IsUsernameEnabled       = true;
            IsPasswordVisible       = false;
            IsFastLoginVisible      = true;
            IsReadWegameInfoVisible = false;
            FastLoginText           = "记住账号";
            UsernameHint            = "盛趣账号";
            UsernameToolTip         = "输入盛趣账号";
            PasswordHint            = "密码";
            ReadWeGameInfoText      = "重新获取账号信息";
            ReadWeGameInfoToolTip   = "勾选后将启动 WeGame 并读取当前启动账号信息";

            switch (loginType)
            {
                case LoginType.Slide:
                    IsFastLoginEnabled = true;
                    break;

                case LoginType.QRCode:
                    IsUsernameVisible  = false;
                    IsFastLoginEnabled = true;
                    break;

                case LoginType.Static:
                    IsPasswordVisible  = true;
                    IsFastLoginEnabled = true;
                    FastLoginText      = "快速登录";
                    break;

                case LoginType.WeGame:
                    IsPasswordVisible       = true;
                    IsFastLoginEnabled      = false;
                    IsFastLogin             = true;
                    IsReadWegameInfoVisible = true;
                    FastLoginText           = "记住账号";
                    ReadWeGameInfoText      = "强制重新抓包";
                    IsReadWegameInfo        = false;
                    UsernameHint            = "WeGame 账号（可选）";
                    UsernameToolTip         = "优先使用已保存账号或自动抓取得到的 WeGame 登录账号";
                    PasswordHint            = "登录令牌（可选）";
                    ReadWeGameInfoToolTip   = "勾选后跳过已保存令牌, 直接重新抓取";
                    break;
            }
        }
        finally
        {
            isApplyingLoginType = false;
        }

        RefreshAccountStatus();
        RefreshStartLoginState();
    }

    private void RefreshAccountStatus()
    {
        var loginType = loginTypeOption?.LoginType ?? LoginType.Slide;
        var accountType = loginType == LoginType.WeGame ?
                              XIVAccountType.WeGame :
                              XIVAccountType.Sdo;

        var exists = !string.IsNullOrWhiteSpace(Username) && App.AccountManager.FindAccount(Username, accountType) != null;

        IsNewAccountBadgeVisible = IsUsernameVisible && !exists && !string.IsNullOrWhiteSpace(Username);
    }

    private void RefreshStartLoginState()
    {
        OnPropertyChanged(nameof(CanStartLogin));
        StartLoginCommand.NotifyCanExecuteChanged();
    }
}
