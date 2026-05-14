using System.Windows.Threading;
using XIVLauncher.Account;
using XIVLauncher.Account.Cred;
using XIVLauncher.Dalamud;
using XIVLauncher.Settings;

namespace XIVLauncher.Startup;

public class StartupContext
{
    public LauncherSettingsV3   Settings              { get; set; } = null!;
    public AccountManager       AccountManager        { get; set; } = null!;
    public DalamudService       Dalamud               { get; set; } = null!;
    public Dispatcher           Dispatcher            { get; set; } = null!;
    public bool                 IsRestartingForUpdate { get; set; }
    public CredTypeApplyResult? CredTypeApplyResult   { get; set; }
}
