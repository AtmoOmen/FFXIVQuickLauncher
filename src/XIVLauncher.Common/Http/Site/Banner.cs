using System;
using Newtonsoft.Json;

namespace XIVLauncher.Common.Http.Site;

public class Banner
{
    [JsonProperty("HomeImagePath")]  public Uri  LsbBanner     { get; set; }
    [JsonProperty("OutLink")]        public Uri  Link          { get; set; }
    [JsonProperty("order_priority")] public int? OrderPriority { get; set; }
    [JsonProperty("fix_order")]      public int? FixOrder      { get; set; }
}
