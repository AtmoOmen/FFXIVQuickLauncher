namespace XIVLauncher.GamePatchV3.Models;

public sealed class IntegrityCheckProgress
{
    public string CurrentFile { get; set; } = string.Empty;

    public int ProcessedFileCount { get; set; }

    public int TotalFileCount { get; set; }

    public string PhaseText { get; set; } = string.Empty;
}
