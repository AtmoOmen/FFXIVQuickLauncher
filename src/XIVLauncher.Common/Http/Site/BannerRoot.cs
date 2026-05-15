using Newtonsoft.Json;

namespace XIVLauncher.Common.Http.Site;

public class BannerRoot
{
    [JsonProperty("Data")]
    public Banner[] Banners { get; set; } = null!;
}
