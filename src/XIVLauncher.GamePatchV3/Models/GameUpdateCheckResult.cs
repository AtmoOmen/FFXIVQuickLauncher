namespace XIVLauncher.GamePatchV3.Models;

public sealed class GameUpdateCheckResult
{
    public bool            NeedsUpdate { get; init; }
    public GameUpdatePlan? UpdatePlan  { get; init; }
}
