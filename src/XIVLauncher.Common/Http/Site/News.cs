using Newtonsoft.Json;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.Common.Http.Site;

public class News
{
    public string Url => Links.SDO_NEWS_ARTICLE_BASE_URL + ID;

    [JsonProperty("PublishDate")]
    public DateTimeOffset Date { get; set; }

    [JsonProperty("Title")]
    public string Title { get; set; } = null!;

    [JsonProperty("Summary")]
    public string Summary { get; set; } = null!;

    [JsonProperty("Id")]
    public string ID { get; set; } = null!;

    [JsonProperty("CategoryCode")]
    public int CategoryCode { get; set; }

    public bool IsAnnouncement => this.CategoryCode is 8324 or 8325 or 8326 or 8327;

    [JsonProperty("tag", NullValueHandling = NullValueHandling.Ignore)]
    public string Tag
    {
        get => field ?? (this.IsAnnouncement ? "Follow-up" : string.Empty);
        set;
    } = null!;
}
