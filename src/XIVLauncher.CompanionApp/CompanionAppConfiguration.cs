namespace XIVLauncher.CompanionApp;

public sealed class CompanionAppConfiguration
{
    public string Name =>
        string.IsNullOrEmpty(FilePath)
            ? "无效程序"
            : $"{(IsExecutable ? "程序" : string.Empty)}: {System.IO.Path.GetFileNameWithoutExtension(FilePath)}";

    public string FilePath { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public bool RunAsAdmin { get; set; }

    public CompanionAppLaunchTrigger LaunchTrigger { get; set; }

    public bool StopWhenGameExits { get; set; }

    public bool CanStopWhenGameExits =>
        !RunAsAdmin && LaunchTrigger == CompanionAppLaunchTrigger.GameLaunch;

    public string? Path
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                FilePath = value;
        }
    }

    public string? CommandLine
    {
        get => null;
        set
        {
            if (value != null)
                Arguments = value;
        }
    }

    public bool RunOnClose
    {
        get => false;
        set
        {
            if (value)
                LaunchTrigger = CompanionAppLaunchTrigger.GameExit;
        }
    }

    public bool KillAfterClose
    {
        get => false;
        set
        {
            if (value)
                StopWhenGameExits = true;
        }
    }

    private bool IsExecutable =>
        !string.IsNullOrEmpty(FilePath) && System.IO.Path.GetExtension(FilePath).Equals(".exe", StringComparison.OrdinalIgnoreCase);
}
