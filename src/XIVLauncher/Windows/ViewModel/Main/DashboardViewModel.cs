using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using XIVLauncher.Common.Constant;
using XIVLauncher.Login;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel.Main;

public sealed class DashboardViewModel : INotifyPropertyChanged
{
    private readonly Action<LoginAfterAction> requestStartGameAction;
    private readonly Action                   requestSwitchAccountAction;
    private readonly Action                   requestOpenDCTravelAction;
    private readonly Action                   requestOpenDeviceProfileAction;
    private readonly Action<LoginArea>        requestSetAreaAction;

    private readonly SyncCommand  startGameCommand;
    private readonly SyncCommand  startGameNoDalamudCommand;
    private readonly SyncCommand  startGameNoPluginsCommand;
    private readonly SyncCommand  startGameNoThirdCommand;
    private readonly AsyncCommand switchAccountCommand;
    private readonly SyncCommand  openDCTravelCommand;
    private readonly SyncCommand  openDeviceProfileCommand;
    private readonly SyncCommand  openPaymentCommand;
    private readonly SyncCommand  openShopCommand;
    private readonly SyncCommand  openOfficialAccountCommand;

    private bool isSwitchingAccount;

    public DashboardViewModel
    (
        Action<LoginAfterAction> requestStartGameAction,
        Action                   requestSwitchAccountAction,
        Action                   requestOpenDCTravelAction,
        Action                   requestOpenDeviceProfileAction,
        Action<LoginArea>        requestSetAreaAction
    )
    {
        this.requestStartGameAction         = requestStartGameAction;
        this.requestSwitchAccountAction     = requestSwitchAccountAction;
        this.requestOpenDCTravelAction      = requestOpenDCTravelAction;
        this.requestOpenDeviceProfileAction = requestOpenDeviceProfileAction;
        this.requestSetAreaAction           = requestSetAreaAction;

        startGameCommand           = new SyncCommand(_ => this.requestStartGameAction(LoginAfterAction.Start));
        startGameNoDalamudCommand  = new SyncCommand(_ => this.requestStartGameAction(LoginAfterAction.StartWithoutDalamud));
        startGameNoPluginsCommand  = new SyncCommand(_ => this.requestStartGameAction(LoginAfterAction.StartWithoutPlugins));
        startGameNoThirdCommand    = new SyncCommand(_ => this.requestStartGameAction(LoginAfterAction.StartWithoutThird));
        switchAccountCommand       = new AsyncCommand(async _ => await SwitchAccount(), () => !isSwitchingAccount);
        openDCTravelCommand        = new SyncCommand(_ => this.requestOpenDCTravelAction());
        openDeviceProfileCommand   = new SyncCommand(_ => this.requestOpenDeviceProfileAction());
        openPaymentCommand         = new SyncCommand(_ => Process.Start(new ProcessStartInfo(Links.SDO_PAYMENT_URL) { UseShellExecute  = true }));
        openShopCommand            = new SyncCommand(_ => Process.Start(new ProcessStartInfo(Links.SDO_SHOPPING_URL) { UseShellExecute = true }));
        openOfficialAccountCommand = new SyncCommand(_ => Process.Start(new ProcessStartInfo(Links.SDO_BILIBILI_URL) { UseShellExecute = true }));

        Areas = [];
    }

    public ICommand StartGameCommand           => startGameCommand;
    public ICommand StartGameNoDalamudCommand  => startGameNoDalamudCommand;
    public ICommand StartGameNoPluginsCommand  => startGameNoPluginsCommand;
    public ICommand StartGameNoThirdCommand    => startGameNoThirdCommand;
    public ICommand SwitchAccountCommand       => switchAccountCommand;
    public ICommand OpenDCTravelCommand        => openDCTravelCommand;
    public ICommand OpenDeviceProfileCommand   => openDeviceProfileCommand;
    public ICommand OpenPaymentCommand         => openPaymentCommand;
    public ICommand OpenShopCommand            => openShopCommand;
    public ICommand OpenOfficialAccountCommand => openOfficialAccountCommand;

    public ObservableCollection<LoginArea> Areas { get; }

    public LoginArea? SelectedArea
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || value == null)
                return;

            requestSetAreaAction(value);
            AreaName   = value.AreaName;
            AreaStatus = value.AreaStatus;
        }
    }

    public string AccountName
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string GameVersion
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string AreaName
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public int AreaStatus
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnPropertyChanged(nameof(IsAreaOnline));
            OnPropertyChanged(nameof(IsAreaMaintenance));
        }
    }

    public bool IsAreaOnline      => AreaStatus != 4;
    public bool IsAreaMaintenance => AreaStatus == 4;

    public bool IsDCTravelUnderMaintenance
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnPropertyChanged(nameof(IsDCTravelAvailable));
        }
    }

    public bool IsDCTravelAvailable => !IsDCTravelUnderMaintenance;

    public bool IsSwitchingAccount
    {
        get => isSwitchingAccount;
        set
        {
            if (!SetProperty(ref isSwitchingAccount, value))
                return;

            switchAccountCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task SwitchAccount()
    {
        IsSwitchingAccount = true;
        await Task.Delay(100);
        requestSwitchAccountAction();
        IsSwitchingAccount = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
