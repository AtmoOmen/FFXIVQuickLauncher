using System.Windows.Threading;
using XIVLauncher.Accounts;
using XIVLauncher.Accounts.Cred;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Settings;

namespace XIVLauncher.Startup;

public class StartupContext
{
    public LauncherSettingsV3   Settings              { get; set; } = null!;
    public AccountManager       AccountManager        { get; set; } = null!;
    public DalamudUpdater       DalamudUpdater        { get; set; } = null!;
    public Dispatcher           Dispatcher            { get; set; } = null!;
    public bool                 IsRestartingForUpdate { get; set; }
    public CredTypeApplyResult? CredTypeApplyResult   { get; set; }
}
