namespace XIVLauncher.GamePatchV3.Models;

public sealed class GamePatchProgress
{
    public string PhaseText      { get; init; } = string.Empty;
    public string CurrentFile    { get; init; } = string.Empty;
    public string StatusText     { get; init; } = string.Empty;
    public long   Progress       { get; init; }
    public long   Total          { get; init; }
    public long   Speed          { get; init; }
    public bool   IsByteProgress { get; init; }
}
