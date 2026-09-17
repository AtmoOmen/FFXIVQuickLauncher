namespace XIVLauncher.GamePatchV3.Update.Models;

public sealed class GamePatchProgress
{
    public string PhaseText      { get; init; } = string.Empty;
    public string CurrentFile    { get; init; } = string.Empty;
    public string StatusText     { get; init; } = string.Empty;
    public double Progress       { get; init; }
    public double Total          { get; init; }
    public long   Speed          { get; init; }
    public bool   IsByteProgress { get; init; }
}
