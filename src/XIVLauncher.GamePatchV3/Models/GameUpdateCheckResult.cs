namespace XIVLauncher.GamePatchV3;

public sealed class GameUpdateCheckResult
{
    public bool            NeedsUpdate { get; init; }
    public GameUpdatePlan? UpdatePlan  { get; init; }
}
