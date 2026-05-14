using XIVLauncher.Account;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Login;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Factories;

public sealed class MainWindowAccountDraftFactory
{
    public static XIVAccount CreatePendingNewAccount(string loginAccount, string sndaId, XIVAccountType accountType, LoginArea? area) =>
        new()
        {
            SdoLoginAccount             = loginAccount,
            WeGameSndaID                = sndaId,
            AccountType                 = accountType,
            AreaName                    = area?.AreaName ?? string.Empty,
            DeviceProfilePresetId       = string.Empty,
            DeviceProfileDynamicEnabled = false,
            IsDeviceProfileRotation     = true,
            DeviceProfileRotationDays   = AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS
        };

    public static XIVAccount CreateIndependentDeviceProfileDraft(XIVAccount account) =>
        new()
        {
            SdoLoginAccount                    = account.SdoLoginAccount,
            WeGameSndaID                       = account.WeGameSndaID,
            AccountType                        = account.AccountType,
            AreaName                           = account.AreaName,
            UserDefinedName                    = account.UserDefinedName,
            DeviceProfilePresetId              = account.DeviceProfilePresetId,
            DeviceProfileDynamicEnabled        = true,
            IsDeviceProfileRotation            = account.IsDeviceProfileRotation,
            DeviceProfileRotationDays          = account.DeviceProfileRotationDays,
            DeviceProfileLastGeneratedUtcTicks = account.DeviceProfileLastGeneratedUtcTicks
        };
}
