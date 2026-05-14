namespace XIVLauncher.Dalamud;

[Serializable]
public sealed class DalamudStartInfo
{
    public string WorkingDirectory  = string.Empty;
    public string ConfigurationPath = string.Empty;
    public string LoggingPath       = string.Empty;

    public string PluginDirectory = string.Empty;
    public string AssetDirectory  = string.Empty;
    public int    DelayInitializeMs;

    public string GameVersion             = string.Empty;
    public string TroubleshootingPackData = string.Empty;
    public string LauncherDirectory       = Environment.CurrentDirectory;
}
