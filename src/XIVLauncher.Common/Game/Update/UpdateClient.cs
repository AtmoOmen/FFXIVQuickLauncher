using System.IO;
using System.Threading.Tasks;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Game.Patch.V3;

namespace XIVLauncher.Common.Game.Update;

public class UpdateClient
{
    /// <summary>
    ///     检查游戏更新
    /// </summary>
    public async Task<LoginResult> Check(LoginArea area, DirectoryInfo gamePath, bool forceBaseVersion)
    {
        _ = area;
        var currentGameVersion = Repository.Ffxiv.GetVer(gamePath);

        using var metadataClient = new V3GamePatchMetadataClient();
        var       updatePlan     = await metadataClient.BuildUpdatePlan(currentGameVersion, forceBaseVersion).ConfigureAwait(false);

        return updatePlan == null
                   ? new LoginResult { PendingPatches = null, V3GameUpdatePlan = null, State       = LoginState.Ok, OAuthLogin             = null }
                   : new LoginResult { PendingPatches = null, V3GameUpdatePlan = updatePlan, State = LoginState.NeedsPatchGame, OAuthLogin = new OAuthLoginResult() };
    }

}
