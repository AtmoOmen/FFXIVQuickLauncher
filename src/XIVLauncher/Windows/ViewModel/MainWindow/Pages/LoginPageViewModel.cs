using System;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Serilog;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Pages;

public sealed class LoginPageViewModel : ViewModelBase
{
    private readonly Func<bool>                                   isBusyFunc;
    private readonly Action<LoginPageViewModel, LoginAfterAction> requestLoginAction;
    private readonly Action                                       requestCancelLoginAction;
    private readonly Action<LoginPageViewModel>                   requestRefreshQrCodeAction;
    private readonly Action                                       requestShowInjectPageAction;
    private readonly Action                                       requestBackToMainPageAction;
    private readonly Action                                       requestFakeStartAction;

    public LoginPageViewModel
    (
        Func<bool>                                   isBusyFunc,
        Action<LoginPageViewModel, LoginAfterAction> requestLoginAction,
        Action                                       requestCancelLoginAction,
        Action<LoginPageViewModel>                   requestRefreshQrCodeAction,
        Action                                       requestShowInjectPageAction,
        Action                                       requestBackToMainPageAction,
        Action                                       requestFakeStartAction
    )
    {
        this.isBusyFunc                  = isBusyFunc;
        this.requestLoginAction          = requestLoginAction;
        this.requestCancelLoginAction    = requestCancelLoginAction;
        this.requestRefreshQrCodeAction  = requestRefreshQrCodeAction;
        this.requestShowInjectPageAction = requestShowInjectPageAction;
        this.requestBackToMainPageAction = requestBackToMainPageAction;
        this.requestFakeStartAction      = requestFakeStartAction;

        LoginTypeOptions = [.. LoginTypeOption.Get(App.Settings.ShowWeGameTokenLogin.GetValueOrDefault(false))];
        loginTypeOption  = LoginTypeOptions.First(x => x.LoginType == App.Settings.SelectedLoginType.GetValueOrDefault(LoginType.Slide));
        ApplyLoginType(loginTypeOption.LoginType);

        StartLoginCommand       = new SyncCommand(_ => this.requestLoginAction(this, LoginAfterAction.Start),               () => !this.isBusyFunc());
        LoginNoStartCommand     = new SyncCommand(_ => this.requestLoginAction(this, LoginAfterAction.UpdateOnly),          () => !this.isBusyFunc());
        LoginNoDalamudCommand   = new SyncCommand(_ => this.requestLoginAction(this, LoginAfterAction.StartWithoutDalamud), () => !this.isBusyFunc());
        LoginNoPluginsCommand   = new SyncCommand(_ => this.requestLoginAction(this, LoginAfterAction.StartWithoutPlugins), () => !this.isBusyFunc());
        LoginNoThirdCommand     = new SyncCommand(_ => this.requestLoginAction(this, LoginAfterAction.StartWithoutThird),   () => !this.isBusyFunc());
        LoginRepairCommand      = new SyncCommand(_ => this.requestLoginAction(this, LoginAfterAction.Repair),              () => !this.isBusyFunc());
        LoginForceQRCommand     = new SyncCommand(_ => this.requestLoginAction(this, LoginAfterAction.ForceQR),             () => !this.isBusyFunc());
        LoginCancelCommand      = new SyncCommand(_ => this.requestCancelLoginAction());
        RefreshQrCodeCommand    = new SyncCommand(_ => this.requestRefreshQrCodeAction(this), () => !this.isBusyFunc() && IsQrCodeExpired);
        InjectModeSwitchCommand = new SyncCommand(_ => this.requestShowInjectPageAction(),    () => !this.isBusyFunc());
        BackToMainPageCommand   = new SyncCommand(_ => this.requestBackToMainPageAction());
        FakeStartCommand        = new SyncCommand(_ => this.requestFakeStartAction(), () => !this.isBusyFunc());
    }

    public ICommand StartLoginCommand { get; }

    public ICommand LoginNoStartCommand { get; }

    public ICommand LoginNoDalamudCommand { get; }

    public ICommand LoginNoPluginsCommand { get; }

    public ICommand LoginNoThirdCommand { get; }

    public ICommand LoginRepairCommand { get; }

    public ICommand LoginCancelCommand { get; }

    public ICommand LoginForceQRCommand { get; }

    public ICommand RefreshQrCodeCommand { get; }

    public ICommand InjectModeSwitchCommand { get; }

    public ICommand BackToMainPageCommand { get; }

    public ICommand FakeStartCommand { get; }

    public LoginTypeOption[] LoginTypeOptions { get; }

    public bool IsAutoLogin
    {
        get => isAutoLogin;
        set => SetProperty(ref isAutoLogin, value);
    }

    public bool IsFastLogin
    {
        get => isFastLogin;
        set => SetProperty(ref isFastLogin, value);
    }

    public bool IsReadWegameInfo
    {
        get => isReadWegameInfo;
        set => SetProperty(ref isReadWegameInfo, value);
    }

    public string Username
    {
        get => username;
        set => SetProperty(ref username, value);
    }

    public string Password
    {
        get => password;
        set => SetProperty(ref password, value);
    }

    public LoginTypeOption LoginTypeOption
    {
        get => loginTypeOption;
        set
        {
            if (!SetProperty(ref loginTypeOption, value))
                return;

            App.Settings.SelectedLoginType = value.LoginType;
            Password                       = string.Empty;
            ApplyLoginType(value.LoginType);
        }
    }

    public int AreaIndex
    {
        set => App.Settings.SelectedServer = value;
    }

    public LoginArea? Area
    {
        get => area;
        set
        {
            var oldArea = area;

            if (!SetProperty(ref area, value))
                return;

            Log.Information("大区变更 {OldArea} -> {NewArea}", oldArea, value);
        }
    }

    public LoginArea[] LoginAreas
    {
        get => loginAreas;
        set => SetProperty(ref loginAreas, value);
    }

    public string LoginMessage
    {
        get => loginMessage;
        set => SetProperty(ref loginMessage, value);
    }

    public BitmapImage? QRCodeBitmapImage
    {
        get => qrCodeBitmapImage;
        set => SetProperty(ref qrCodeBitmapImage, value);
    }

    public bool IsQrCodeExpired
    {
        get => isQrCodeExpired;
        set
        {
            if (!SetProperty(ref isQrCodeExpired, value))
                return;

            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsUsernameVisible
    {
        get => isUsernameVisible;
        private set => SetProperty(ref isUsernameVisible, value);
    }

    public bool IsPasswordVisible
    {
        get => isPasswordVisible;
        private set => SetProperty(ref isPasswordVisible, value);
    }

    public bool IsFastLoginVisible
    {
        get => isFastLoginVisible;
        private set => SetProperty(ref isFastLoginVisible, value);
    }

    public bool IsReadWegameInfoVisible
    {
        get => isReadWegameInfoVisible;
        private set => SetProperty(ref isReadWegameInfoVisible, value);
    }

    public string FastLoginText
    {
        get => fastLoginText;
        private set => SetProperty(ref fastLoginText, value);
    }

    public string UsernameHint
    {
        get => usernameHint;
        private set => SetProperty(ref usernameHint, value);
    }

    public string PasswordHint
    {
        get => passwordHint;
        private set => SetProperty(ref passwordHint, value);
    }

    public void SelectLoginType(LoginType loginType)
    {
        var option = LoginTypeOptions.FirstOrDefault(x => x.LoginType == loginType);

        if (option != null)
            LoginTypeOption = option;
    }

    private void ApplyLoginType(LoginType loginType)
    {
        IsUsernameVisible       = true;
        IsPasswordVisible       = false;
        IsFastLoginVisible      = true;
        IsReadWegameInfoVisible = false;
        FastLoginText           = "快速登录";
        UsernameHint            = "盛趣账号";
        PasswordHint            = "密码";

        switch (loginType)
        {
            case LoginType.Slide:
                break;

            case LoginType.QRCode:
                IsUsernameVisible = false;
                break;

            case LoginType.Static:
                IsPasswordVisible = true;
                FastLoginText     = "保存密码";
                break;

            case LoginType.WeGameToken:
                IsPasswordVisible = true;
                UsernameHint      = "SndaId";
                PasswordHint      = "抓包Token";
                break;

            case LoginType.WeGameSID:
                IsFastLoginVisible      = false;
                IsReadWegameInfoVisible = true;
                UsernameHint            = "从Wegame自动获取的账号";
                break;
        }
    }

    private bool            isAutoLogin;
    private bool            isFastLogin;
    private bool            isReadWegameInfo;
    private string          username        = string.Empty;
    private string          password        = string.Empty;
    private LoginTypeOption loginTypeOption = null!;
    private LoginArea?      area;
    private LoginArea[]     loginAreas   = [];
    private string          loginMessage = string.Empty;
    private BitmapImage?    qrCodeBitmapImage;
    private bool            isQrCodeExpired;
    private bool            isUsernameVisible = true;
    private bool            isPasswordVisible;
    private bool            isFastLoginVisible = true;
    private bool            isReadWegameInfoVisible;
    private string          fastLoginText = "快速登录";
    private string          usernameHint  = "盛趣账号";
    private string          passwordHint  = "密码";
}
