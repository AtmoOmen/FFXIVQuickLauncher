#nullable enable
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace XIVLauncher.Support;

public class GitHubSource : Velopack.Sources.GithubSource
{
    private static readonly SemaphoreSlim _semaphore = new(3, 3);

    public GitHubSource(string repoUrl, string? accessToken, bool prerelease, IFileDownloader? downloader = null)
        : base(repoUrl, accessToken, prerelease, downloader)
    {
    }

    public override async Task<VelopackAssetFeed> GetReleaseFeed(ILogger logger, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var releases = await GetReleases(Prerelease).ConfigureAwait(false);
        if (releases == null || releases.Length == 0)
        {
            logger.Warn($"No releases found at '{RepoUri}'.");
            return new VelopackAssetFeed();
        }

        var releasesFileName = GetVeloReleaseIndexName(channel);
        var entries = new List<GitBaseAsset>();

        var releasesList = releases.Select((SemanticVersion?, GithubRelease) (r) =>
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
        });

        if (latestLocalRelease == null)
        {
            releasesList = releasesList.Take(1);
        }
        else
        {
            releasesList = releasesList.Where(v => v.Item1 != null && v.Item1 >= latestLocalRelease.Version);
        }

        var jsonList = releasesList.Select(async Task<(GithubRelease, string?)> (r) =>
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
            await _semaphore.WaitAsync().ConfigureAwait(false); // 限制并发下载
            var releaseBytes = await Downloader.DownloadBytes(assetUrl, Authorization, "application/octet-stream").ConfigureAwait(false);
            _semaphore.Release();
            return (r.Item2, RemoveByteOrderMarkerIfPresent(releaseBytes));
        }).ToList();

        foreach (var j in jsonList)
        {
            var (r, txt) = await j.ConfigureAwait(false);
            if (txt is null)
            {
                continue;
            }

            var feed = VelopackAssetFeed.FromJson(txt);
            foreach (var f in feed.Assets)
            {
                entries.Add(new GitBaseAsset(f, r));
            }
        }

        return new VelopackAssetFeed
        {
            Assets = entries.Cast<VelopackAsset>().ToArray(),
        };
    }

    public static string GetVeloReleaseIndexName(string channel)
    {
        return $"releases.{channel ?? VelopackRuntimeInfo.SystemOs.GetOsShortName()}.json";
    }
    public static string RemoveByteOrderMarkerIfPresent(byte[] content)
    {
        byte[] output = { };

        Func<byte[], byte[], bool> matches = (bom, src) =>
        {
            if (src.Length < bom.Length)
                return false;

            return !bom.Where((chr, index) => src[index] != chr).Any();
        };

        var utf32Be = new byte[] { 0x00, 0x00, 0xFE, 0xFF };
        var utf32Le = new byte[] { 0xFF, 0xFE, 0x00, 0x00 };
        var utf16Be = new byte[] { 0xFE, 0xFF };
        var utf16Le = new byte[] { 0xFF, 0xFE };
        var utf8 = new byte[] { 0xEF, 0xBB, 0xBF };

        if (matches(utf32Be, content))
        {
            output = new byte[content.Length - utf32Be.Length];
        }
        else if (matches(utf32Le, content))
        {
            output = new byte[content.Length - utf32Le.Length];
        }
        else if (matches(utf16Be, content))
        {
            output = new byte[content.Length - utf16Be.Length];
        }
        else if (matches(utf16Le, content))
        {
            output = new byte[content.Length - utf16Le.Length];
        }
        else if (matches(utf8, content))
        {
            output = new byte[content.Length - utf8.Length];
        }
        else
        {
            output = content;
        }

        if (output.Length > 0)
        {
            Buffer.BlockCopy(content, content.Length - output.Length, output, 0, output.Length);
        }

        return Encoding.UTF8.GetString(output);
    }

}

internal static class LoggerExtensions
{
    public static void Trace(this ILogger logger, string message)
    {
        logger.LogTrace(message);
    }

    public static void Trace(this ILogger logger, Exception ex, string message)
    {
        logger.LogTrace(ex, message);
    }
    public static void Trace(this ILogger logger, Exception ex)
    {
        logger.LogTrace(ex, ex.Message);
    }

    public static void Debug(this ILogger logger, string message)
    {
        logger.LogDebug(message);
    }

    public static void Debug(this ILogger logger, Exception ex, string message)
    {
        logger.LogDebug(ex, message);
    }

    public static void Debug(this ILogger logger, Exception ex)
    {
        logger.LogDebug(ex, ex.Message);
    }

    public static void Info(this ILogger logger, string message)
    {
        logger.LogInformation(message);
    }

    public static void Info(this ILogger logger, Exception ex, string message)
    {
        logger.LogInformation(ex, message);
    }

    public static void Info(this ILogger logger, Exception ex)
    {
        logger.LogInformation(ex, ex.Message);
    }

    public static void Warn(this ILogger logger, string message)
    {
        logger.LogWarning(message);
    }

    public static void Warn(this ILogger logger, Exception ex, string message)
    {
        logger.LogWarning(ex, message);
    }

    public static void Warn(this ILogger logger, Exception ex)
    {
        logger.LogWarning(ex, ex.Message);
    }

    public static void Error(this ILogger logger, string message)
    {
        logger.LogError(message);
    }

    public static void Error(this ILogger logger, Exception ex, string message)
    {
        logger.LogError(ex, message);
    }

    public static void Error(this ILogger logger, Exception ex)
    {
        logger.LogError(ex, ex.Message);
    }

    public static void Fatal(this ILogger logger, string message)
    {
        logger.LogCritical(message);
    }

    public static void Fatal(this ILogger logger, Exception ex, string message)
    {
        logger.LogCritical(ex, message);
    }

    public static void Fatal(this ILogger logger, Exception ex)
    {
        logger.LogCritical(ex, ex.Message);
    }
}
