using XIVLauncher.Accounts;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Login;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Factories;

public sealed class MainWindowAccountDraftFactory
{
    public static XIVAccount CreatePendingNewAccount(string loginAccount, string sndaId, XIVAccountType accountType, LoginArea? area) =>
        new()
        {
            LoginAccount                = loginAccount,
            SndaId                      = sndaId,
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
}
