using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVLauncher.Common.Constant;
using XIVLauncher.Login;

namespace XIVLauncher.Windows.ViewModel.Main;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly Action<LoginAfterAction> requestStartGameAction;
    private readonly Action                   requestSwitchAccountAction;
    private readonly Action                   requestOpenDCTravelAction;
    private readonly Action                   requestOpenDeviceProfileAction;
    private readonly Action<LoginArea>        requestSetAreaAction;

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

        Areas = [];
    }

    public ObservableCollection<LoginArea> Areas { get; }

    [ObservableProperty]
    public partial bool IsSwitchingAccount { get; set; }

    partial void OnIsSwitchingAccountChanged(bool value) =>
        SwitchAccountCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    public partial string AccountName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GameVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AreaName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAreaOnline))]
    [NotifyPropertyChangedFor(nameof(IsAreaMaintenance))]
    public partial int AreaStatus { get; set; }

    public bool IsAreaOnline      => AreaStatus != 4;
    public bool IsAreaMaintenance => AreaStatus == 4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDCTravelAvailable))]
    public partial bool IsDCTravelUnderMaintenance { get; set; }

    public bool IsDCTravelAvailable => !IsDCTravelUnderMaintenance;

    public LoginArea? SelectedArea
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            if (value == null)
                return;

            requestSetAreaAction(value);
            AreaName   = value.AreaName;
            AreaStatus = value.AreaStatus;
        }
    }

    [RelayCommand]
    private void StartGame() =>
        requestStartGameAction(LoginAfterAction.Start);

    [RelayCommand]
    private void StartGameNoDalamud() =>
        requestStartGameAction(LoginAfterAction.StartWithoutDalamud);

    [RelayCommand]
    private void StartGameNoPlugins() =>
        requestStartGameAction(LoginAfterAction.StartWithoutPlugins);

    [RelayCommand]
    private void StartGameNoThird() =>
        requestStartGameAction(LoginAfterAction.StartWithoutThird);

    [RelayCommand(CanExecute = nameof(CanSwitchAccount))]
    private async Task SwitchAccount()
    {
        IsSwitchingAccount = true;
        await Task.Delay(100);
        requestSwitchAccountAction();
        IsSwitchingAccount = false;
    }

    private bool CanSwitchAccount() =>
        !IsSwitchingAccount;

    [RelayCommand]
    private void OpenDCTravel() =>
        requestOpenDCTravelAction();

    [RelayCommand]
    private void OpenDeviceProfile() =>
        requestOpenDeviceProfileAction();

    [RelayCommand]
    private void OpenPayment() =>
        Process.Start(new ProcessStartInfo(Links.SDO_PAYMENT_URL) { UseShellExecute = true });

    [RelayCommand]
    private void OpenShop() =>
        Process.Start(new ProcessStartInfo(Links.SDO_SHOPPING_URL) { UseShellExecute = true });

    [RelayCommand]
    private void OpenOfficialAccount() =>
        Process.Start(new ProcessStartInfo(Links.SDO_BILIBILI_URL) { UseShellExecute = true });
}
