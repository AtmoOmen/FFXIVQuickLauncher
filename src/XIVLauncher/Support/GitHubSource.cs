using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NuGet.Versioning;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.Support;

public class GitHubSource
(
    string           repoUrl,
    string?          accessToken,
    bool             prerelease,
    string?          proxyBaseUrl = null,
    IFileDownloader? downloader   = null
)
    :
        GithubSource(repoUrl, accessToken, prerelease, downloader)
{
    private readonly string? proxyUrl = proxyBaseUrl?.TrimEnd('/');

    // copy from Velopack
    public static string GetVeloReleaseIndexName(string channel)
        => $"releases.{channel ?? VelopackRuntimeInfo.SystemOs.GetOsShortName()}.json";

    // copy from Velopack
    public static string RemoveByteOrderMarkerIfPresent(byte[] content)
    {
        byte[] output = [];

        var utf32Be = new byte[] { 0x00, 0x00, 0xFE, 0xFF };
        var utf32Le = new byte[] { 0xFF, 0xFE, 0x00, 0x00 };
        var utf16Be = new byte[] { 0xFE, 0xFF };
        var utf16Le = new byte[] { 0xFF, 0xFE };
        var utf8    = new byte[] { 0xEF, 0xBB, 0xBF };

        if (Matches(utf32Be, content))
            output = new byte[content.Length - utf32Be.Length];
        else if (Matches(utf32Le, content))
            output = new byte[content.Length - utf32Le.Length];
        else if (Matches(utf16Be, content))
            output = new byte[content.Length - utf16Be.Length];
        else if (Matches(utf16Le, content))
            output = new byte[content.Length - utf16Le.Length];
        else if (Matches(utf8, content))
            output = new byte[content.Length - utf8.Length];
        else
            output = content;

        if (output.Length > 0)
            Buffer.BlockCopy(content, content.Length - output.Length, output, 0, output.Length);

        return Encoding.UTF8.GetString(output);

        bool Matches(byte[] bom, byte[] src)
        {
            if (src.Length < bom.Length)
                return false;

            return !bom.Where((chr, index) => src[index] != chr).Any();
        }
    }

    public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var releases = await GetReleases(Prerelease).ConfigureAwait(false);

        if (releases == null || releases.Length == 0)
        {
            logger.Warn($"No releases found at '{RepoUri}'.");
            return new VelopackAssetFeed();
        }

        var releasesFileName = GetVeloReleaseIndexName(channel);
        var entries          = new List<GitBaseAsset>();

        var releasesList = releases.Select
        ((SemanticVersion?, GithubRelease) (r) =>
            {
                // 从PR名字解析版本号，与ci-workflow.yml中的vpk upload github --releaseName "Release $refver"关联
                var match = Regex.Match(r.Name ?? string.Empty, @"\d+.\d+.\d+", RegexOptions.Compiled);

                if (!match.Success)
                {
                    logger.Warn($"Failed to parse version from release name '{r.Name}'.");
                    return (null, r);
                }

                try
                {
                    return (SemanticVersion.Parse(match.Value), r);
                }
                catch (Exception ex)
                {
                    logger.Error(ex);
                    return (null, r);
                }
            }
        ).ToList();

        if (latestLocalRelease is not null && releasesList.Any(v => v.Item1 is not null && v.Item1 == latestLocalRelease.Version))
            releasesList = releasesList.Where(v => v.Item1 != null && v.Item1 >= latestLocalRelease.Version).ToList();
        else
            releasesList = releasesList.Take(1).ToList();

        var jsonList = releasesList.Select
        (async Task<(GithubRelease, string?)> (r) =>
            {
                // this might be a browser url or an api url (depending on whether we have a AccessToken or not)
                // https://docs.github.com/en/rest/reference/releases#get-a-release-asset
                string assetUrl;

                try
                {
                    assetUrl = GetAssetUrlFromName(r.Item2, releasesFileName);
                }
                catch (Exception ex)
                {
                    logger.Trace(ex.ToString());
                    return (r.Item2, null);
                }

                var releaseBytes = await Downloader.DownloadBytes
                                   (
                                       assetUrl,
                                       CreateHeaders(Authorization, "application/octet-stream")
                                   ).ConfigureAwait(false);
                return (r.Item2, RemoveByteOrderMarkerIfPresent(releaseBytes));
            }
        ).ToList();

        foreach (var j in jsonList)
        {
            var result = await j.ConfigureAwait(false);
            var r      = result.Item1;
            var txt    = result.Item2;
            if (txt is null)
                continue;

            var feed = VelopackAssetFeed.FromJson(txt);
            foreach (var f in feed.Assets)
                entries.Add(new GitBaseAsset(f, r));
        }

        return new VelopackAssetFeed
        {
            Assets = entries.Cast<VelopackAsset>().ToArray()
        };
    }

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        // 1. 计算原始的 GitHub API 路径
        // RepoUri.AbsolutePath 通常是 "/User/Repo"
        const int PER_PAGE     = 5;
        const int PAGE         = 1;
        var       releasesPath = $"repos{RepoUri.AbsolutePath}/releases?per_page={PER_PAGE}&page={PAGE}";

        // 2. 构建目标 URL
        string requestUrl;

        if (!string.IsNullOrEmpty(proxyUrl))
            requestUrl = $"{proxyUrl}/{Links.GITHUB_API_BASE_URL.TrimEnd('/')}/{releasesPath}";
        else
        {
            // 直连逻辑 (回退到原始方式)
            var baseUri = GetApiBaseUrl(RepoUri);
            requestUrl = new Uri(baseUri, releasesPath).ToString();
        }

        // 3. 发起请求
        // 如果走了代理，Worker 会返回修改后的 JSON（里面的 browser_download_url 都会变成代理链接）
        var response = await Downloader.DownloadString
                       (
                           requestUrl,
                           CreateHeaders(Authorization, "application/vnd.github.v3+json")
                       ).ConfigureAwait(false);

        var releases = JsonConvert.DeserializeObject<List<GithubRelease>>(response);
        return releases == null ? [] : releases.OrderByDescending(d => d.PublishedAt).Where(x => includePrereleases || !x.Prerelease).ToArray();
    }

    protected override string GetAssetUrlFromName(GithubRelease release, string assetName)
    {
        if (!string.IsNullOrEmpty(proxyUrl))
        {
            var asset = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (asset == null)
                throw new Exception($"Could not find asset '{assetName}' in release '{release.Name}'");

            return asset.BrowserDownloadUrl;
        }

        return base.GetAssetUrlFromName(release, assetName);
    }

    private static Dictionary<string, string> CreateHeaders((string Name, string Value)? authorization, string accept)
    {
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = accept
        };

        if (authorization is { } header && !string.IsNullOrWhiteSpace(header.Name) && !string.IsNullOrWhiteSpace(header.Value))
            headers[header.Name] = header.Value;

        return headers;
    }
}
