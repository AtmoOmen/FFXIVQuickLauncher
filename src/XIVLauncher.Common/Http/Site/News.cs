using Newtonsoft.Json;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.Common.Http.Site;

public class News
{
    public string Url => Links.SDO_NEWS_ARTICLE_BASE_URL + Id;

    [JsonProperty("PublishDate")]
    public DateTimeOffset Date { get; set; }

    [JsonProperty("Title")]
    public string Title { get; set; }

    [JsonProperty("Id")]
    public string Id { get; set; }

    [JsonProperty("tag", NullValueHandling = NullValueHandling.Ignore)]
    public string Tag { get; set; }
}
