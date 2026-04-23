namespace XIVLauncher.Windows.GameClientFiles;

public sealed class GameClientFileTaskItemSnapshot
{
    public string Title { get; init; } = string.Empty;

    public double Progress { get; init; }

    public bool IsIndeterminate { get; init; }
}
