using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.Game.Patch.V3;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Game.Update;

public class UpdateClient
(
    HttpClient mockHttpClient
)
{
    private HttpClient MockHttpClient { get; set; } = mockHttpClient;

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

    public async Task<LoginResult> CheckLegacy(LoginArea area, DirectoryInfo gamePath, bool forceBaseVersion)
    {
        var request = new HttpRequestMessage
        (
            HttpMethod.Post,
            $"http://{area.AreaPatch}/http/win32/shanda_release_chs_game/{(forceBaseVersion ? Constants.BASE_GAME_VERSION : Repository.Ffxiv.GetVer(gamePath))}"
        );

        request.Headers.AddWithoutValidation("X-Hash-Check", "enabled");
        request.Headers.AddWithoutValidation("User-Agent",   Constants.PatcherUserAgent);

        EnsureVersionSanity(gamePath, Constants.MaxExpansion);
        request.Content = new StringContent(GetVersionReport(gamePath, Constants.MaxExpansion, forceBaseVersion));

        var resp = await MockHttpClient.SendAsync(request).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.Conflict)
            return new LoginResult { PendingPatches = null, State = LoginState.NeedsPatchBoot, OAuthLogin = null };

        if (!resp.Headers.TryGetValues("X-Patch-Unique-Id", out var uidVals))
        {
            Log.Error("RequestUri: {RequestRequestUri}", request.RequestUri);
            Log.Error("Content: {RequestContent}",       request.Content);
            Log.Error("Response: {Text}",                text);

            throw new InvalidResponseException("无法获取 X-Patch-Unique-Id.", text);
        }

        _ = uidVals.First();

        if (string.IsNullOrEmpty(text))
            return new LoginResult { PendingPatches = null, State = LoginState.Ok, OAuthLogin = null };

        Log.Verbose("需要更新游戏\n补丁一览:\n{PatchList}", text);

        var pendingPatches = PatchListParser.Parse(text);
        return new LoginResult { PendingPatches = pendingPatches, V3GameUpdatePlan = null, State = LoginState.NeedsPatchGame, OAuthLogin = new OAuthLoginResult() };
    }

    private static void EnsureVersionSanity(DirectoryInfo gamePath, int exLevel)
    {
        var failed = IsBadVersionSanity(gamePath, Repository.Ffxiv);
        failed |= IsBadVersionSanity(gamePath, Repository.Ffxiv, true);

        if (exLevel >= 1)
        {
            failed |= IsBadVersionSanity(gamePath, Repository.Ex1);
            failed |= IsBadVersionSanity(gamePath, Repository.Ex1, true);
        }

        if (exLevel >= 2)
        {
            failed |= IsBadVersionSanity(gamePath, Repository.Ex2);
            failed |= IsBadVersionSanity(gamePath, Repository.Ex2, true);
        }

        if (exLevel >= 3)
        {
            failed |= IsBadVersionSanity(gamePath, Repository.Ex3);
            failed |= IsBadVersionSanity(gamePath, Repository.Ex3, true);
        }

        if (exLevel >= 4)
        {
            failed |= IsBadVersionSanity(gamePath, Repository.Ex4);
            failed |= IsBadVersionSanity(gamePath, Repository.Ex4, true);
        }

        if (exLevel >= 5)
        {
            failed |= IsBadVersionSanity(gamePath, Repository.Ex5);
            failed |= IsBadVersionSanity(gamePath, Repository.Ex5, true);
        }

        if (failed)
            throw new InvalidVersionFilesException();
    }

    private static bool IsBadVersionSanity(DirectoryInfo gamePath, Repository repo, bool isBck = false)
    {
        var text = repo.GetVer(gamePath, isBck);

        var nullOrWhitespace = string.IsNullOrWhiteSpace(text);
        var containsNewline  = text.Contains('\n');
        var allNullBytes     = Encoding.UTF8.GetBytes(text).All(x => x == 0x00);

        if (nullOrWhitespace || containsNewline || allNullBytes)
        {
            Log.Error("版本检查失败, 状态: {Repo}/{IsBck}: {NullOrWhitespace}, {ContainsNewline}, {AllNullBytes}", repo, isBck, nullOrWhitespace, containsNewline, allNullBytes);
            return true;
        }

        return false;
    }

    private static string GetVersionReport(DirectoryInfo gamePath, int exLevel, bool forceBaseVersion)
    {
        var verReport = "ffxivboot.exe/149504/5f2a70612aa58378eb347869e75adeb8f5581a1b\n";

        if (exLevel >= 1)
            verReport += $"ex1\t{(forceBaseVersion ? Constants.BASE_GAME_VERSION : Repository.Ex1.GetVer(gamePath))}\n";

        if (exLevel >= 2)
            verReport += $"ex2\t{(forceBaseVersion ? Constants.BASE_GAME_VERSION : Repository.Ex2.GetVer(gamePath))}\n";

        if (exLevel >= 3)
            verReport += $"ex3\t{(forceBaseVersion ? Constants.BASE_GAME_VERSION : Repository.Ex3.GetVer(gamePath))}\n";

        if (exLevel >= 4)
            verReport += $"ex4\t{(forceBaseVersion ? Constants.BASE_GAME_VERSION : Repository.Ex4.GetVer(gamePath))}\n";

        if (exLevel >= 5)
            verReport += $"ex5\t{(forceBaseVersion ? Constants.BASE_GAME_VERSION : Repository.Ex5.GetVer(gamePath))}\n";

        return verReport;
    }
}
