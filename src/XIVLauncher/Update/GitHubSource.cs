#if !XL_NOAUTOUPDATE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NuGet.Versioning;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
using XIVLauncher.Common.Constant;
using XIVLauncher.Support;

namespace XIVLauncher.Update;

public class GitHubSource
(
    string           repoUrl,
    bool             prerelease,
    string           proxyBaseUrl,
    IFileDownloader? downloader = null
)
    : IUpdateSource
{
    private readonly IFileDownloader            downloader  = downloader ?? new XLHttpClientFileDownloader();
    private readonly string                     proxyUrl    = proxyBaseUrl.TrimEnd('/');
    private readonly Uri                        repoUri     = new(repoUrl);
    private readonly Dictionary<string, string> packageUrls = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new SnakeCaseNamingStrategy()
        }
    };

    public async Task<VelopackAssetFeed> GetReleaseFeed
    (
        IVelopackLogger logger,
        string?         appId,
        string          channel,
        Guid?           stagingId          = null,
        VelopackAsset?  latestLocalRelease = null
    )
    {
        var releases = await GetReleases().ConfigureAwait(false);

        if (releases.Count == 0)
        {
            logger.Warn($"No releases found at '{repoUri}'.");
            return new();
        }

        var releaseIndexName = GetVeloReleaseIndexName(channel);
        var feedAssets       = new List<VelopackAsset>();

        foreach (var (version, release) in PickReleases(releases, latestLocalRelease, logger))
        {
            _ = version;
            var releaseIndexUrl = GetReleaseAssetUrl(release, releaseIndexName);
            if (string.IsNullOrEmpty(releaseIndexUrl))
                continue;

            var releaseBytes = await downloader.DownloadBytes
                               (
                                   releaseIndexUrl,
                                   CreateHeaders("application/octet-stream")
                               ).ConfigureAwait(false);
            var feed = VelopackAssetFeed.FromJson(RemoveByteOrderMarkerIfPresent(releaseBytes));

            foreach (var asset in feed.Assets)
            {
                var packageUrl = GetReleaseAssetUrl(release, asset.FileName);

                if (string.IsNullOrEmpty(packageUrl))
                {
                    logger.Warn($"Could not find asset '{asset.FileName}' in release '{release.Name}'.");
                    continue;
                }

                packageUrls[asset.FileName] = packageUrl;
                feedAssets.Add(asset);
            }
        }

        return new()
        {
            Assets = feedAssets.ToArray()
        };
    }

    public async Task DownloadReleaseEntry
    (
        IVelopackLogger   logger,
        VelopackAsset     releaseEntry,
        string            localFile,
        Action<int>       progress,
        CancellationToken cancelToken
    )
    {
        if (!packageUrls.TryGetValue(releaseEntry.FileName, out var packageUrl))
            throw new InvalidOperationException($"缺少更新包下载地址: {releaseEntry.FileName}");

        logger.Info($"Downloading '{releaseEntry.FileName}' from '{packageUrl}'.");
        await downloader.DownloadFile
        (
            packageUrl,
            localFile,
            progress,
            CreateHeaders("application/octet-stream"),
            cancelToken: cancelToken
        ).ConfigureAwait(false);
    }

    private async Task<List<GithubRelease>> GetReleases()
    {
        const int PER_PAGE = 5;
        const int PAGE     = 1;
        var       path     = $"repos{repoUri.AbsolutePath}/releases?per_page={PER_PAGE}&page={PAGE}";
        var       url      = $"{proxyUrl}/{Links.GITHUB_API_BASE_URL.TrimEnd('/')}/{path}";
        var       json     = await downloader.DownloadString(url, CreateHeaders("application/vnd.github.v3+json")).ConfigureAwait(false);
        var       releases = JsonConvert.DeserializeObject<List<GithubRelease>>(json, JsonSettings);

        return releases == null
                   ? []
                   : releases.OrderByDescending(release => release.PublishedAt)
                             .Where(release => prerelease || !release.Prerelease)
                             .ToList();
    }

    private static List<(SemanticVersion Version, GithubRelease Release)> PickReleases
    (
        IReadOnlyList<GithubRelease> releases,
        VelopackAsset?               latestLocalRelease,
        IVelopackLogger              logger
    )
    {
        var parsed = new List<(SemanticVersion Version, GithubRelease Release)>(releases.Count);

        foreach (var release in releases)
        {
            var match = Regex.Match(release.Name ?? string.Empty, @"\d+.\d+.\d+", RegexOptions.Compiled);

            if (!match.Success)
            {
                logger.Warn($"Failed to parse version from release name '{release.Name}'.");
                continue;
            }

            try
            {
                parsed.Add((SemanticVersion.Parse(match.Value), release));
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }

        if (latestLocalRelease != null && parsed.Any(entry => entry.Item1 == latestLocalRelease.Version))
            return parsed.Where(entry => entry.Version                    >= latestLocalRelease.Version).ToList();

        return parsed.Take(1).ToList();
    }

    private string? GetReleaseAssetUrl(GithubRelease release, string assetName)
    {
        var asset = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, assetName, StringComparison.OrdinalIgnoreCase));
        if (asset == null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            return null;

        return ToProxyUrl(asset.BrowserDownloadUrl);
    }

    private string ToProxyUrl(string url) =>
        url.StartsWith($"{proxyUrl}/", StringComparison.OrdinalIgnoreCase) ? url : $"{proxyUrl}/{url}";

    private Dictionary<string, string> CreateHeaders(string accept)
    {
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = accept
        };

        return headers;
    }

    public static string GetVeloReleaseIndexName(string channel) =>
        $"releases.{channel ?? VelopackRuntimeInfo.SystemOs.GetOsShortName()}.json";

    public static string RemoveByteOrderMarkerIfPresent(byte[] content)
    {
        byte[] output;

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

        bool Matches(byte[] bom, byte[] source)
        {
            if (source.Length < bom.Length)
                return false;

            return !bom.Where((chr, index) => source[index] != chr).Any();
        }
    }
}
#endif
