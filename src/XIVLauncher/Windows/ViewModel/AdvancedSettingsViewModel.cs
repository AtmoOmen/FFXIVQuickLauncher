using Serilog.Events;
using XIVLauncher.Common.Support;

namespace XIVLauncher.Windows.ViewModel;

public class AdvancedSettingsViewModel : ViewModelBase
{
    public bool ExitLauncherAfterGameExit
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool TreatNonZeroExitCodeAsFailure
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool EnableVerboseLog
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool EnableSkipUpdate
    {
        get;
        set => SetProperty(ref field, value);
    }

    public void Load()
    {
        ExitLauncherAfterGameExit     = App.Settings.ExitLauncherAfterGameExit     ?? true;
        TreatNonZeroExitCodeAsFailure = App.Settings.TreatNonZeroExitCodeAsFailure ?? false;
        EnableSkipUpdate              = App.Settings.EnableSkipUpdate              ?? false;
        EnableVerboseLog              = LogInit.LevelSwitch.MinimumLevel == LogEventLevel.Verbose;
    }

    public void Save()
    {
        App.Settings.ExitLauncherAfterGameExit     = ExitLauncherAfterGameExit;
        App.Settings.TreatNonZeroExitCodeAsFailure = TreatNonZeroExitCodeAsFailure;
        App.Settings.EnableSkipUpdate              = EnableSkipUpdate;
        App.Settings.EnableVerboseLog              = EnableVerboseLog;
        LogInit.LevelSwitch.MinimumLevel           = EnableVerboseLog ? LogEventLevel.Verbose : LogInit.GetDefaultLevel();
    }
}
