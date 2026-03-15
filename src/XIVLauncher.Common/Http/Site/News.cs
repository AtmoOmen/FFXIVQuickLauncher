using System;
using Newtonsoft.Json;

namespace XIVLauncher.Common.Http.Site;

public class News
{
    public string Url => $"https://ff.web.sdo.com/web8/index.html#/newstab/newscont/{Id}";

    [JsonProperty("PublishDate")] public DateTimeOffset Date  { get; set; }
    [JsonProperty("Title")]       public string         Title { get; set; }
    [JsonProperty("Id")]          public string         Id    { get; set; }

    [JsonProperty("tag", NullValueHandling = NullValueHandling.Ignore)]
    public string Tag { get; set; }
}
