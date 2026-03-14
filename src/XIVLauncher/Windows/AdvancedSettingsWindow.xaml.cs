using System.Windows;
using Serilog.Events;
using XIVLauncher.Common.Support;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for FirstTimeSetup.xaml
/// </summary>
public partial class AdvancedSettingsWindow
{
    public bool WasCompleted { get; private set; } = false;

    public AdvancedSettingsWindow()
    {
        InitializeComponent();

        DataContext = new AdvancedSettingsViewModel();
        Load();
    }

    private void Load()
    {
        ExitLauncherAfterGameExitCheckbox.IsChecked     = App.Settings.ExitLauncherAfterGameExit     ?? true;
        TreatNonZeroExitCodeAsFailureCheckbox.IsChecked = App.Settings.TreatNonZeroExitCodeAsFailure ?? false;
        EnableSkipUpdate.IsChecked                      = App.Settings.EnableSkipUpdate              ?? false;
        EnableVerboseLog.IsChecked                      = LogInit.LevelSwitch.MinimumLevel == LogEventLevel.Verbose;
    }

    private void Save()
    {
        App.Settings.ExitLauncherAfterGameExit     = ExitLauncherAfterGameExitCheckbox.IsChecked     == true;
        App.Settings.TreatNonZeroExitCodeAsFailure = TreatNonZeroExitCodeAsFailureCheckbox.IsChecked == true;
        App.Settings.EnableSkipUpdate              = EnableSkipUpdate.IsChecked                      == true;
        App.Settings.EnableVerboseLog              = EnableVerboseLog.IsChecked                      == true;
        LogInit.LevelSwitch.MinimumLevel           = EnableVerboseLog.IsChecked == true ? LogEventLevel.Verbose : LogInit.GetDefaultLevel();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Save();
        Close();
    }
}
