using Serilog;
using XIVLauncher.Common;

namespace XIVLauncher.GamePatchV3;

public static class GameUpdater
{
    public static async Task<GameUpdateCheckResult> Check(DirectoryInfo gamePath, bool forceBaseVersion, CancellationToken cancellationToken = default)
    {
        var currentGameVersion = Repository.Ffxiv.GetVer(gamePath).Trim().Trim('\uFEFF').Trim();
        Log.Information("[UpdateClient] 当前游戏版本 {CurrentGameVersion}, 强制基线 {ForceBaseVersion}", currentGameVersion, forceBaseVersion);

        using var metadataClient = new GamePatchMetadataClient();
        var       updatePlan     = await metadataClient.BuildUpdatePlan(gamePath, currentGameVersion, forceBaseVersion, cancellationToken).ConfigureAwait(false);

        Log.Information("[UpdateClient] 更新检查结果, 需要更新 {NeedsUpdate}", updatePlan != null);
        return new GameUpdateCheckResult { NeedsUpdate = updatePlan != null, UpdatePlan = updatePlan };
    }
}
