using CommunityToolkit.Mvvm.ComponentModel;
using Serilog.Events;
using XIVLauncher.Support;

namespace XIVLauncher.Windows.ViewModel;

public partial class AdvancedSettingsViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool TreatNonZeroExitCodeAsFailure { get; set; }

    [ObservableProperty]
    public partial bool EnableVerboseLog { get; set; }

    [ObservableProperty]
    public partial bool EnableSkipUpdate { get; set; }

    public void Load()
    {
        TreatNonZeroExitCodeAsFailure = App.Settings.TreatNonZeroExitCodeAsFailure;
        EnableSkipUpdate              = App.Settings.EnableSkipUpdate;
        EnableVerboseLog              = LogInit.LevelSwitch.MinimumLevel == LogEventLevel.Verbose;
    }

    public void Save()
    {
        App.Settings.Update
        (settings =>
            {
                settings.TreatNonZeroExitCodeAsFailure = TreatNonZeroExitCodeAsFailure;
                settings.EnableSkipUpdate              = EnableSkipUpdate;
                settings.EnableVerboseLog              = EnableVerboseLog;
            }
        );

        LogInit.LevelSwitch.MinimumLevel = EnableVerboseLog ?
                                               LogEventLevel.Verbose :
                                               LogInit.GetDefaultLevel();
    }
}
