#nullable enable
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Velopack.Sources;

namespace XIVLauncher.Support;

public class GitHubSource : Velopack.Sources.GithubSource
{
    public GitHubSource(string repoUrl, string? accessToken, bool prerelease, IFileDownloader? downloader = null)
        : base(repoUrl, accessToken, prerelease, downloader)
    {
    }

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        // https://docs.github.com/en/rest/reference/releases
        const int perPage = 1;
        const int page = 1;
        var releasesPath = $"repos{RepoUri.AbsolutePath}/releases?per_page={perPage}&page={page}";
        var baseUri = GetApiBaseUrl(RepoUri);
        var getReleasesUri = new Uri(baseUri, releasesPath);
        var response = await Downloader.DownloadString(getReleasesUri.ToString(), Authorization, "application/vnd.github.v3+json").ConfigureAwait(false);
        var releases = JsonConvert.DeserializeObject<List<GithubRelease>>(response);
        if (releases == null)
            return [];
        return releases.OrderByDescending(d => d.PublishedAt).Where(x => includePrereleases || !x.Prerelease).ToArray();
    }
}
