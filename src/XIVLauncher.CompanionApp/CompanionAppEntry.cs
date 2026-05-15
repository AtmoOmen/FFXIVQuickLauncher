namespace XIVLauncher.CompanionApp;

public sealed class CompanionAppEntry
{
    public bool IsEnabled { get; set; }

    public CompanionAppConfiguration CompanionApp { get; set; } = new();

    public CompanionAppConfiguration? Addon
    {
        get => null;
        set
        {
            if (value != null)
                CompanionApp = value;
        }
    }
}
