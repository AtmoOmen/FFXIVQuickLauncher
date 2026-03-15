using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Encryption;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Game;

public partial class Launcher
{
    public LoginClient LoginClient    { get; } = new();
    public HttpClient  MockHttpClient { get; } = new(new HttpClientHandler { UseCookies = true });

    public Process? LaunchGame
    (
        IGameRunner   runner,
        string        sessionID,
        string        sndaID,
        int           dcTravelPort,
        string        areaID,
        string        lobbyHost,
        string        gmHost,
        string        dbHost,
        string        areasInfo,
        string        additionalArguments,
        DirectoryInfo gamePath,
        bool          encryptArguments,
        DpiAwareness  dpiAwareness
    )
    {
        Log.Information("[Launcher] 启动游戏 (参数: {AdditionalArguments})", additionalArguments);
        
        var exePath     = Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe");
        var environment = new Dictionary<string, string>();
        
        var argumentBuilder = new ArgumentBuilder()
                              .Append("-AppID",                     "100001900")
                              .Append("-AreaID",                    areaID)
                              .Append("Dev.LobbyHost01",            lobbyHost)
                              .Append("Dev.LobbyPort01",            "54994")
                              .Append("Dev.GMServerHost",           gmHost)
                              .Append("Dev.SaveDataBankHost",       dbHost)
                              .Append("resetConfig",                "0")
                              .Append("DEV.MaxEntitledExpansionID", "1")
                              .Append("DEV.TestSID",                sessionID)
                              .Append("XL.SndaId",                  sndaID)
                              .Append("XL.LobbyHosts",              areasInfo)
                              .Append("XL.DcTraveler",              $"{dcTravelPort}");

        if (!string.IsNullOrEmpty(additionalArguments))
        {
            foreach (Match match in AdditionalArgumentsRegex().Matches(additionalArguments))
                argumentBuilder.Append(match.Groups["key"].Value, match.Groups["value"].Value);
        }

        if (!File.Exists(exePath))
            throw new BinaryNotPresentException(exePath);

        var workingDir = Path.Combine(gamePath.FullName, "game");
        var arguments = encryptArguments
                            ? argumentBuilder.BuildEncrypted()
                            : argumentBuilder.Build();

        return runner.Start(exePath, workingDir, arguments, environment, dpiAwareness);
    }
    
    /// <summary>
    /// 假装以游戏官方启动器的身份下载资源
    /// </summary>
    public async Task<byte[]> DownloadAsLauncher(string url, string contentType = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (!string.IsNullOrEmpty(contentType))
            request.Headers.AddWithoutValidation("Accept", contentType);

        request.Headers.AddWithoutValidation("Accept-Encoding", "gzip, deflate");
        request.Headers.AddWithoutValidation("Accept-Language", "zh-CN");
        request.Headers.AddWithoutValidation
        (
            "User-Agent",
            "Mozilla/4.0 (compatible; MSIE 7.0; Windows NT 6.2; WOW64; Trident/7.0; .NET4.0C; .NET4.0E; .NET CLR 2.0.50727; .NET CLR 3.0.30729; .NET CLR 3.5.30729)"
        );
        request.Headers.AddWithoutValidation("Referer", "https://ff.web.sdo.com/project/launcher0904/index.html");

        var resp = await MockHttpClient.SendAsync(request).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }
    
    /// <summary>
    /// 检查游戏更新
    /// </summary>
    public async Task<LoginResult> CheckGameUpdate(LoginArea area, DirectoryInfo gamePath, bool forceBaseVersion)
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
            throw new InvalidResponseException("Could not get X-Patch-Unique-Id.", text);
        }

        _ = uidVals.First();

        if (string.IsNullOrEmpty(text))
            return new LoginResult { PendingPatches = null, State = LoginState.Ok, OAuthLogin = null };

        Log.Verbose("Game Patching is needed... List:\n{PatchList}", text);

        var pendingPatches = PatchListParser.Parse(text);
        return new LoginResult { PendingPatches = pendingPatches, State = LoginState.NeedsPatchGame, OAuthLogin = new OAuthLoginResult() };
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
            Log.Error("Sanity check failed for {Repo}/{IsBck}: {NullOrWhitespace}, {ContainsNewline}, {AllNullBytes}", repo, isBck, nullOrWhitespace, containsNewline, allNullBytes);
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

    [GeneratedRegex(@"\s*(?<key>[^=]+)\s*=\s*(?<value>[^\s]+)\s*", RegexOptions.Compiled)]
    private static partial Regex AdditionalArgumentsRegex();
}
