namespace XIVLauncher.Common.Game.Integrity;

public sealed class IntegrityCheckProgress
{
    public string CurrentFile { get; set; } = string.Empty;

    public int ProcessedFileCount { get; set; }

    public int TotalFileCount { get; set; }

    public string PhaseText { get; set; } = string.Empty;
}
