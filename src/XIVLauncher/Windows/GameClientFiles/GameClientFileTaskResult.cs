namespace XIVLauncher.Windows.GameClientFiles;

public sealed class GameClientFileTaskResult
{
    public required GameClientFileTaskResultStatus Status { get; init; }

    public bool ShouldLaunchGame { get; init; }
}
