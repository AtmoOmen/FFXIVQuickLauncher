using System.Text.RegularExpressions;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Game;

public partial class Launcher
{
    public RestartMonitor RestartMonitor { get; } = new();
    public HttpClient     MockHttpClient { get; } = new(new HttpClientHandler { UseCookies = true });

    public FFXIVProcess? LaunchGame
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
        DPIAwareness  dpiAwareness
    )
    {
        Log.Information("[Launcher] 启动游戏 (参数: {AdditionalArguments})", additionalArguments);

        var exePath     = Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe");
        var environment = new Dictionary<string, string>();

        var argumentBuilder = new ArgumentBuilder()
                              .Append("-AppID",                     SdoInfos.APP_ID)
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

        var process = runner.Start(exePath, workingDir, arguments, environment, dpiAwareness);
        return process != null ? new FFXIVProcess(process) : null;
    }

    /// <summary>
    ///     假装以游戏官方启动器的身份下载资源
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
        request.Headers.AddWithoutValidation("Referer", Links.SDO_LAUNCHER_REFERER_URL);

        var resp = await MockHttpClient.SendAsync(request).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    [GeneratedRegex(@"\s*(?<key>[^=]+)\s*=\s*(?<value>[^\s]+)\s*", RegexOptions.Compiled)]
    private static partial Regex AdditionalArgumentsRegex();
}
