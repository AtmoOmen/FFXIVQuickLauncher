using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Serilog.Events;
using XIVLauncher.Common.Support;

namespace XIVLauncher.Windows.ViewModel;

public class AdvancedSettingsViewModel : INotifyPropertyChanged
{
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

        LogInit.LevelSwitch.MinimumLevel = EnableVerboseLog ? LogEventLevel.Verbose : LogInit.GetDefaultLevel();
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}
