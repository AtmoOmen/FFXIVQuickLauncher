using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVLauncher.Common.Constant;
using XIVLauncher.Login.Models;

namespace XIVLauncher.Windows.ViewModel.Main;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly Action<LoginAfterAction>   requestStartGameAction;
    private readonly Action                     requestSwitchAccountAction;
    private readonly Action                     requestOpenDCTravelAction;
    private readonly Action                     requestOpenDeviceProfileAction;
    private readonly Func<string, string, Task> requestOpenAuthenticatedSiteAction;
    private readonly Action<LoginArea>          requestSetAreaAction;

    public DashboardViewModel
    (
        Action<LoginAfterAction>   requestStartGameAction,
        Action                     requestSwitchAccountAction,
        Action                     requestOpenDCTravelAction,
        Action                     requestOpenDeviceProfileAction,
        Func<string, string, Task> requestOpenAuthenticatedSiteAction,
        Action<LoginArea>          requestSetAreaAction
    )
    {
        this.requestStartGameAction             = requestStartGameAction;
        this.requestSwitchAccountAction         = requestSwitchAccountAction;
        this.requestOpenDCTravelAction          = requestOpenDCTravelAction;
        this.requestOpenDeviceProfileAction     = requestOpenDeviceProfileAction;
        this.requestOpenAuthenticatedSiteAction = requestOpenAuthenticatedSiteAction;
        this.requestSetAreaAction               = requestSetAreaAction;

        Areas = [];
    }

    public ObservableCollection<LoginArea> Areas { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchAccountCommand))]
    public partial bool IsSwitchingAccount { get; set; }

    [ObservableProperty]
    public partial string AccountName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GameVersion { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartGameButtonText))]
    public partial bool IsGameUpdateAvailable { get; set; }

    public string StartGameButtonText => IsGameUpdateAvailable ?
                                             "更新游戏" :
                                             "启动游戏";

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
    private Task OpenPayment() =>
        requestOpenAuthenticatedSiteAction(Links.SDO_PAYMENT_URL, SdoInfos.PAYMENT_APP_ID);

    [RelayCommand]
    private Task OpenShop() =>
        requestOpenAuthenticatedSiteAction(Links.SDO_SHOPPING_URL, SdoInfos.SHOPPING_APP_ID);

    [RelayCommand]
    private Task OpenGiftsStation() =>
        requestOpenAuthenticatedSiteAction(Links.SDO_GIFTS_STATION_URL, SdoInfos.PAYMENT_APP_ID);

    [RelayCommand]
    private Task OpenRisingStones() =>
        requestOpenAuthenticatedSiteAction
        (
            $"{Links.RISING_STONE_LOGIN_URL}?redirectUrl={Uri.EscapeDataString(Links.RISING_STONE_URL)}",
            SdoInfos.RISING_STONES_APP_ID
        );

    [RelayCommand]
    private Task OpenSupplyStation() =>
        requestOpenAuthenticatedSiteAction(Links.SDO_SUPPLY_STATION_URL, SdoInfos.APP_ID);

    [RelayCommand]
    private static void OpenExternalSite
    (
        string? url
    )
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        Process.Start
        (
            new ProcessStartInfo(url)
            {
                UseShellExecute = true
            }
        );
    }
}
