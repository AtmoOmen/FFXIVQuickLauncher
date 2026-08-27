using CommunityToolkit.Mvvm.ComponentModel;
using XIVLauncher.CompanionApp;

namespace XIVLauncher.Windows.ViewModel;

public sealed partial class CompanionAppSetupWindowViewModel : ObservableObject
{
    public bool CanStopWhenGameExits => !RunAsAdmin && LaunchTrigger == CompanionAppLaunchTrigger.GameLaunch;

    [ObservableProperty]
    public partial string FilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Arguments { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStopWhenGameExits))]
    public partial bool RunAsAdmin { get; set; }

    partial void OnRunAsAdminChanged
    (
        bool value
    )
    {
        if (value)
            StopWhenGameExits = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchOnGameStart))]
    [NotifyPropertyChangedFor(nameof(LaunchOnGameExit))]
    [NotifyPropertyChangedFor(nameof(CanStopWhenGameExits))]
    public partial CompanionAppLaunchTrigger LaunchTrigger { get; set; } = CompanionAppLaunchTrigger.GameLaunch;

    partial void OnLaunchTriggerChanged
    (
        CompanionAppLaunchTrigger value
    )
    {
        if (value != CompanionAppLaunchTrigger.GameLaunch)
            StopWhenGameExits = false;
    }

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

    [ObservableProperty]
    public partial bool StopWhenGameExits { get; set; }

    public void Load
    (
        CompanionAppConfiguration? companionApp
    )
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
}
