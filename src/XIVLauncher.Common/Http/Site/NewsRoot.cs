using Newtonsoft.Json;

namespace XIVLauncher.Common.Http.Site;

public class NewsRoot
{
    [JsonProperty("Data")] public News[] News { get; set; }
}
