using System.Windows.Threading;
using XIVLauncher.Accounts;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Settings;

namespace XIVLauncher.Startup;

public class StartupContext
{
    public ILauncherSettingsV3 Settings              { get; set; } = null!;
    public AccountManager      AccountManager        { get; set; } = null!;
    public DalamudUpdater      DalamudUpdater        { get; set; } = null!;
    public Dispatcher          Dispatcher            { get; set; } = null!;
    public bool                IsUpdateFinished      { get; set; }
    public bool                IsRestartingForUpdate { get; set; }
    public bool                InjectMode            { get; set; }
    public bool                IsDisableAutologin    { get; set; }
}
