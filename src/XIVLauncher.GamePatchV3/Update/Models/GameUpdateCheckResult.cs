namespace XIVLauncher.GamePatchV3.Update.Models;

public sealed class GameUpdateCheckResult
{
    public bool            NeedsUpdate { get; init; }
    public GameUpdatePlan? UpdatePlan  { get; init; }
}
