using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace XIVLauncher.Common.Game;

public partial class Headlines
{
    [JsonProperty("news")] public News[] News { get; set; }

    [JsonProperty("topics")] public News[] Topics { get; set; }

    [JsonProperty("pinned")] public News[] Pinned { get; set; }

    [JsonProperty("banner")] public Banner[] Banner { get; set; }
}

public class Banner
{
    [JsonProperty("HomeImagePath")] public Uri LsbBanner { get; set; }

    [JsonProperty("OutLink")] public Uri Link { get; set; }

    [JsonProperty("order_priority")] public int? OrderPriority { get; set; }

    [JsonProperty("fix_order")] public int? FixOrder { get; set; }
}

public class BannerRoot
{
    [JsonProperty("banner")] public List<Banner> Banner { get; set; }
}

public class News
{
    public string Url => $"https://ff.web.sdo.com/web8/index.html#/newstab/newscont/{Id}";

    [JsonProperty("PublishDate")] public DateTimeOffset Date { get; set; }

    [JsonProperty("Title")] public string Title { get; set; }

    [JsonProperty("Id")] public string Id { get; set; }

    [JsonProperty("tag", NullValueHandling = NullValueHandling.Ignore)]
    public string Tag { get; set; }
}

public class SdoBanner
{
    public Banner[] Data { get; set; }
}

public class SdoNews
{
    public News[] Data { get; set; }
}

public partial class Headlines
{
    public static async Task<Headlines> GetHeadlines(Launcher game)
    {
        var headlines = new Headlines
        {
            Banner = await GetBanner(game),
            News   = await GetNews(game)
        };
        return headlines;
    }

    private static async Task<Banner[]> GetBanner(Launcher game)
    {
        var json = Encoding.UTF8.GetString
        (
            await game.DownloadAsLauncher("https://cqnews.web.sdo.com/api/news/newsList?gameCode=ff&CategoryCode=5203&pageIndex=0&pageSize=8", "*/*").ConfigureAwait
                (false)
        );
        var sdoBanner = JsonConvert.DeserializeObject<SdoBanner>(json);
        return sdoBanner.Data;
    }

    private static async Task<News[]> GetNews(Launcher game)
    {
        var json = Encoding.UTF8.GetString
        (
            await game.DownloadAsLauncher
                ("https://cqnews.web.sdo.com/api/news/newsList?gameCode=ff&CategoryCode=5310,5311,5312,5313,5316&pageIndex=0&pageSize=12", "*/*").ConfigureAwait
                (false)
        );
        var sdoNews = JsonConvert.DeserializeObject<SdoNews>(json);
        return sdoNews.Data;
    }
}
