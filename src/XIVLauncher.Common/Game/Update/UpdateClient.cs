using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Game.Patch.V3;

namespace XIVLauncher.Common.Game.Update;

public static class UpdateClient
{
    /// <summary>
    ///     检查游戏更新
    /// </summary>
    public static async Task<LoginResult> Check(DirectoryInfo gamePath, bool forceBaseVersion, CancellationToken cancellationToken = default)
    {
        var currentGameVersion = Repository.Ffxiv.GetVer(gamePath);

        using var metadataClient = new V3GamePatchMetadataClient();
        var       updatePlan     = await metadataClient.BuildUpdatePlan(currentGameVersion, forceBaseVersion, cancellationToken).ConfigureAwait(false);

        return updatePlan == null
                   ? new LoginResult { PendingPatches = null, V3GameUpdatePlan = null, State       = LoginState.Ok, OAuthLogin             = null }
                   : new LoginResult { PendingPatches = null, V3GameUpdatePlan = updatePlan, State = LoginState.NeedsPatchGame, OAuthLogin = new OAuthLoginResult() };
    }

}
