using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Common.Http.Site;

public partial class Headlines
{
    [JsonProperty("news")]
    public News[] News { get; set; }

    [JsonProperty("topics")]
    public News[] Topics { get; set; }

    [JsonProperty("pinned")]
    public News[] Pinned { get; set; }

    [JsonProperty("banner")]
    public Banner[] Banner { get; set; }
}

public partial class Headlines
{
    public static async Task<Headlines> GetHeadlinesAsync(Launcher game)
    {
        var banners = await GetBannersAsync(game);
        var bannerTitles = new HashSet<string>(banners
            .Where(banner => !string.IsNullOrWhiteSpace(banner.Title))
            .Select(banner => banner.Title), StringComparer.Ordinal);
        var bannerNewsIds = new HashSet<int>(banners
            .Select(banner => banner.NewsId)
            .Where(newsId => newsId.HasValue)
            .Select(newsId => newsId!.Value));

        var headlines = new Headlines
        {
            Banner = banners,
            News = (await GetNewsAsync(game))
                .Where(news => !IsBannerNews(news, bannerTitles, bannerNewsIds))
                .ToArray()
        };

        return headlines;
    }

    private static bool IsBannerNews(News news, HashSet<string> bannerTitles, HashSet<int> bannerNewsIds)
    {
        return bannerTitles.Contains(news.Title) ||
               int.TryParse(news.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var newsId) &&
               bannerNewsIds.Contains(newsId);
    }

    private static async Task<Banner[]> GetBannersAsync(Launcher game)
    {
        var json = Encoding.UTF8.GetString
        (
            await game.DownloadAsLauncher(Links.SDO_NEWS_BANNER_API_URL, "*/*").ConfigureAwait(false)
        );

        var sdoBanner = JsonConvert.DeserializeObject<BannerRoot>(json);
        return sdoBanner.Banners;
    }

    private static async Task<News[]> GetNewsAsync(Launcher game)
    {
        var json = Encoding.UTF8.GetString
        (
            await game.DownloadAsLauncher(Links.SDO_NEWS_LIST_API_URL, "*/*").ConfigureAwait(false)
        );

        var sdoNews = JsonConvert.DeserializeObject<NewsRoot>(json);
        return sdoNews.News;
    }
}
