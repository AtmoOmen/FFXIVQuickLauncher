using System.Globalization;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Common.Http.Site;

public partial class Headlines
{
    private const          int       NEWS_HTTP_ATTEMPTS    = 3;
    private static readonly TimeSpan NEWS_HTTP_RETRY_DELAY = TimeSpan.FromSeconds(2);

    [JsonProperty("news")]
    public required News[] News { get; set; }

    [JsonProperty("topics")]
    public News[] Topics { get; set; } = null!;

    [JsonProperty("pinned")]
    public News[] Pinned { get; set; } = null!;

    [JsonProperty("banner")]
    public Banner[] Banner { get; set; } = null!;
}

public partial class Headlines
{
    public static async Task<Headlines> GetHeadlinesAsync(Launcher game)
    {
        Banner[] banners;

        try
        {
            banners = await GetBannersAsync(game).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "轮播列表获取失败, 本次新闻不做轮播去重");
            banners = [];
        }

        var bannerTitles = new HashSet<string>
        (
            banners
                .Where(banner => !string.IsNullOrWhiteSpace(banner.Title))
                .Select(banner => banner.Title),
            StringComparer.Ordinal
        );
        var bannerNewsIds = new HashSet<int>
        (
            banners
                .Select(banner => banner.NewsId)
                .Where(newsId => newsId.HasValue)
                .Select(newsId => newsId!.Value)
        );

        var headlines = new Headlines
        {
            Banner = banners,
            News = (await GetNewsAsync(game).ConfigureAwait(false))
                   .Where(news => !IsBannerNews(news, bannerTitles, bannerNewsIds))
                   .ToArray()
        };

        return headlines;
    }

    private static bool IsBannerNews(News news, HashSet<string> bannerTitles, HashSet<int> bannerNewsIds) =>
        bannerTitles.Contains(news.Title) || int.TryParse(news.ID, NumberStyles.Integer, CultureInfo.InvariantCulture, out var newsId) && bannerNewsIds.Contains(newsId);

    private static async Task<Banner[]> GetBannersAsync(Launcher game)
    {
        var json = await DownloadTextWithRetryAsync(game, Links.SDO_NEWS_BANNER_API_URL).ConfigureAwait(false);

        var sdoBanner = JsonConvert.DeserializeObject<BannerRoot>(json);
        return sdoBanner?.Banners ?? [];
    }

    private static async Task<News[]> GetNewsAsync(Launcher game)
    {
        var json = await DownloadTextWithRetryAsync(game, Links.SDO_NEWS_LIST_API_URL).ConfigureAwait(false);

        var sdoNews = JsonConvert.DeserializeObject<NewsRoot>(json);
        return sdoNews?.News ?? [];
    }

    private static async Task<string> DownloadTextWithRetryAsync(Launcher game, string url)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= NEWS_HTTP_ATTEMPTS; attempt++)
        {
            try
            {
                var bytes = await game.DownloadAsLauncher(url, "*/*").ConfigureAwait(false);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                lastException = ex;

                if (attempt < NEWS_HTTP_ATTEMPTS)
                {
                    Log.Warning(ex, "获取新闻数据失败 (第 {Attempt}/{Attempts} 次): {Url}", attempt, NEWS_HTTP_ATTEMPTS, url);
                    await Task.Delay(NEWS_HTTP_RETRY_DELAY).ConfigureAwait(false);
                }
            }
        }

        throw new HttpRequestException($"新闻数据获取失败: {url}", lastException);
    }
}
