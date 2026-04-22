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

        LoginTypeOptions = [.. LoginTypeOption.Get()];
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
        get;
        set => SetProperty(ref field, value);
    }

    public bool IsFastLogin
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool IsReadWegameInfo
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            if (loginTypeOption.LoginType == LoginType.WeGameAuto)
                ApplyWeGameSidMode();
        }
    }

    public string Username
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string Password
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public LoginTypeOption LoginTypeOption
    {
        get => loginTypeOption;
        set
        {
            var previousGroup = loginTypeOption?.Group;

            if (!SetProperty(ref loginTypeOption, value))
                return;

            App.Settings.SelectedLoginType = value.LoginType;
            if (previousGroup.HasValue && previousGroup.Value != value.Group)
                Username = string.Empty;
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
        get;
        set
        {
            var oldArea = field;

            if (!SetProperty(ref field, value))
                return;

            Log.Information("大区变更 {OldArea} -> {NewArea}", oldArea, value);
        }
    }

    public LoginArea[] LoginAreas
    {
        get;
        set => SetProperty(ref field, value);
    } = [];

    public string LoginMessage
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public BitmapImage? QRCodeBitmapImage
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool IsQrCodeExpired
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsUsernameVisible
    {
        get;
        private set => SetProperty(ref field, value);
    } = true;

    public bool IsUsernameEnabled
    {
        get;
        private set => SetProperty(ref field, value);
    } = true;

    public bool IsPasswordVisible
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool IsFastLoginVisible
    {
        get;
        private set => SetProperty(ref field, value);
    } = true;

    public bool IsReadWegameInfoVisible
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public string FastLoginText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "快速登录";

    public string UsernameHint
    {
        get;
        private set => SetProperty(ref field, value);
    } = "盛趣账号";

    public string UsernameToolTip
    {
        get;
        private set => SetProperty(ref field, value);
    } = "输入盛趣账号";

    public string PasswordHint
    {
        get;
        private set => SetProperty(ref field, value);
    } = "密码";

    public string ReadWeGameInfoText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "重新获取账号信息";

    public string ReadWeGameInfoToolTip
    {
        get;
        private set => SetProperty(ref field, value);
    } = "勾选后启动 WeGame 并读取当前启动账号的 SndaID 和 SID";

    public void SelectLoginType(LoginType loginType)
    {
        var option = LoginTypeOptions.FirstOrDefault(x => x.LoginType == loginType);

        if (option != null)
            LoginTypeOption = option;
    }
    
    private LoginTypeOption loginTypeOption = null!;

    private void ApplyLoginType(LoginType loginType)
    {
        IsUsernameVisible       = true;
        IsUsernameEnabled       = true;
        IsPasswordVisible       = false;
        IsFastLoginVisible      = true;
        IsReadWegameInfoVisible = false;
        FastLoginText           = "快速登录";
        UsernameHint            = "盛趣账号";
        UsernameToolTip         = "输入盛趣账号";
        PasswordHint            = "密码";
        ReadWeGameInfoText      = "重新获取账号信息";
        ReadWeGameInfoToolTip   = "勾选后将启动 WeGame 并读取当前启动账号信息";

        switch (loginType)
        {
            case LoginType.Slide:
                break;

            case LoginType.QRCode:
                IsUsernameVisible = false;
                break;

            case LoginType.Static:
                IsPasswordVisible = true;
                FastLoginText     = "静态密码";
                break;

            case LoginType.WeGameManual:
                IsPasswordVisible = true;
                UsernameHint      = "SndaID";
                UsernameToolTip   = "输入 WeGame 账号对应的 SndaID";
                PasswordHint      = "登录令牌";
                break;

            case LoginType.WeGameAuto:
                IsFastLoginVisible      = false;
                IsReadWegameInfoVisible = true;
                IsReadWegameInfo        = string.IsNullOrWhiteSpace(Username);
                ApplyWeGameSidMode();
                break;
        }
    }

    private void ApplyWeGameSidMode()
    {
        IsUsernameEnabled = !IsReadWegameInfo;

        if (IsReadWegameInfo)
        {
            UsernameHint          = "将从 WeGame 重新读取账号信息";
            UsernameToolTip       = "将启动 WeGame 并读取当前启动账号信息";
            ReadWeGameInfoToolTip = "取消勾选后, 按输入的 SndaID 查找本地已保存账号信息并尝试登录";
            return;
        }

        UsernameHint          = "SndaID";
        UsernameToolTip       = "输入 WeGame 账号对应的 SndaID";
        ReadWeGameInfoToolTip = "勾选后将启动 WeGame 并读取当前启动账号信息";
    }
}
