using System.ComponentModel;
using System.Runtime.CompilerServices;
using XIVLauncher.CompanionApp;

namespace XIVLauncher.Windows.ViewModel;

public sealed class CompanionAppSetupWindowViewModel : INotifyPropertyChanged
{
    public bool CanStopWhenGameExits => !RunAsAdmin && LaunchTrigger == CompanionAppLaunchTrigger.GameLaunch;

    public string FilePath
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string Arguments
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public bool RunAsAdmin
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            if (value)
                StopWhenGameExits = false;

            OnPropertyChanged(nameof(CanStopWhenGameExits));
        }
    }

    public CompanionAppLaunchTrigger LaunchTrigger
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            if (value != CompanionAppLaunchTrigger.GameLaunch)
                StopWhenGameExits = false;

            OnPropertyChanged(nameof(LaunchOnGameStart));
            OnPropertyChanged(nameof(LaunchOnGameExit));
            OnPropertyChanged(nameof(CanStopWhenGameExits));
        }
    } = CompanionAppLaunchTrigger.GameLaunch;

    public bool LaunchOnGameStart
    {
        get => LaunchTrigger == CompanionAppLaunchTrigger.GameLaunch;
        set
        {
            if (value)
                LaunchTrigger = CompanionAppLaunchTrigger.GameLaunch;
        }
    }

    public bool LaunchOnGameExit
    {
        get => LaunchTrigger == CompanionAppLaunchTrigger.GameExit;
        set
        {
            if (value)
                LaunchTrigger = CompanionAppLaunchTrigger.GameExit;
        }
    }

    public bool StopWhenGameExits
    {
        get;
        set => SetProperty(ref field, value);
    }

    public void Load(CompanionAppConfiguration? companionApp)
    {
        if (companionApp == null)
            return;

        FilePath          = companionApp.FilePath;
        Arguments         = companionApp.Arguments;
        RunAsAdmin        = companionApp.RunAsAdmin;
        LaunchTrigger     = companionApp.LaunchTrigger;
        StopWhenGameExits = companionApp.StopWhenGameExits;
    }

    public CompanionAppConfiguration? BuildResult()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return null;

        return new CompanionAppConfiguration
        {
            FilePath          = FilePath,
            Arguments         = Arguments,
            RunAsAdmin        = RunAsAdmin,
            LaunchTrigger     = LaunchTrigger,
            StopWhenGameExits = LaunchTrigger == CompanionAppLaunchTrigger.GameLaunch && !RunAsAdmin && StopWhenGameExits
        };
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
