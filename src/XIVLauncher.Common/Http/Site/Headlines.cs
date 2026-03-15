using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Common.Http.Site;

public partial class Headlines
{
    [JsonProperty("news")]   public News[]   News   { get; set; }
    [JsonProperty("topics")] public News[]   Topics { get; set; }
    [JsonProperty("pinned")] public News[]   Pinned { get; set; }
    [JsonProperty("banner")] public Banner[] Banner { get; set; }
}

public partial class Headlines
{
    public static async Task<Headlines> GetHeadlinesAsync(Launcher game)
    {
        var headlines = new Headlines
        {
            Banner = await GetBannersAsync(game),
            News   = await GetNewsAsync(game)
        };

        return headlines;
    }

    private static async Task<Banner[]> GetBannersAsync(Launcher game)
    {
        var json = Encoding.UTF8.GetString
        (
            await game.DownloadAsLauncher("https://cqnews.web.sdo.com/api/news/newsList?gameCode=ff&CategoryCode=5203&pageIndex=0&pageSize=8", "*/*").ConfigureAwait
                (false)
        );

        var sdoBanner = JsonConvert.DeserializeObject<BannerRoot>(json);
        return sdoBanner.Banners;
    }

    private static async Task<News[]> GetNewsAsync(Launcher game)
    {
        var json = Encoding.UTF8.GetString
        (
            await game.DownloadAsLauncher
                ("https://cqnews.web.sdo.com/api/news/newsList?gameCode=ff&CategoryCode=5310,5311,5312,5313,5316&pageIndex=0&pageSize=12", "*/*").ConfigureAwait
                (false)
        );

        var sdoNews = JsonConvert.DeserializeObject<NewsRoot>(json);
        return sdoNews.News;
    }
}
