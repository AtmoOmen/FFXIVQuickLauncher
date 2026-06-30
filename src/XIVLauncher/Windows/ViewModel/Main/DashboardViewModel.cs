using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using XIVLauncher.Common.Constant;
using XIVLauncher.Login;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel.Main;

public sealed class DashboardViewModel : INotifyPropertyChanged
{
    public SyncCommand  StartGameCommand           { get; }
    public SyncCommand  StartGameNoDalamudCommand  { get; }
    public SyncCommand  StartGameNoPluginsCommand  { get; }
    public SyncCommand  StartGameNoThirdCommand    { get; }
    public AsyncCommand SwitchAccountCommand       { get; }
    public SyncCommand  OpenDCTravelCommand        { get; }
    public SyncCommand  OpenDeviceProfileCommand   { get; }
    public SyncCommand  OpenPaymentCommand         { get; }
    public SyncCommand  OpenShopCommand            { get; }
    public SyncCommand  OpenOfficialAccountCommand { get; }
    
    private readonly Action<LoginAfterAction> requestStartGameAction;
    private readonly Action                   requestSwitchAccountAction;
    private readonly Action                   requestOpenDCTravelAction;
    private readonly Action                   requestOpenDeviceProfileAction;
    private readonly Action<LoginArea>        requestSetAreaAction;

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

        StartGameCommand           = new(_ => this.requestStartGameAction(LoginAfterAction.Start));
        StartGameNoDalamudCommand  = new(_ => this.requestStartGameAction(LoginAfterAction.StartWithoutDalamud));
        StartGameNoPluginsCommand  = new(_ => this.requestStartGameAction(LoginAfterAction.StartWithoutPlugins));
        StartGameNoThirdCommand    = new(_ => this.requestStartGameAction(LoginAfterAction.StartWithoutThird));
        SwitchAccountCommand       = new(async _ => await SwitchAccount(), () => !isSwitchingAccount);
        OpenDCTravelCommand        = new(_ => this.requestOpenDCTravelAction());
        OpenDeviceProfileCommand   = new(_ => this.requestOpenDeviceProfileAction());
        OpenPaymentCommand         = new(_ => Process.Start(new ProcessStartInfo(Links.SDO_PAYMENT_URL) { UseShellExecute  = true }));
        OpenShopCommand            = new(_ => Process.Start(new ProcessStartInfo(Links.SDO_SHOPPING_URL) { UseShellExecute = true }));
        OpenOfficialAccountCommand = new(_ => Process.Start(new ProcessStartInfo(Links.SDO_BILIBILI_URL) { UseShellExecute = true }));

        Areas = [];
    }

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

            SwitchAccountCommand.RaiseCanExecuteChanged();
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
