using System.Globalization;
using Newtonsoft.Json;

namespace XIVLauncher.Common.Http.Site;

public class Banner
{
    [JsonProperty("Title")]
    public string Title { get; set; }

    [JsonProperty("HomeImagePath")]
    public Uri LsbBanner { get; set; }

    [JsonProperty("OutLink")]
    public Uri Link { get; set; }

    [JsonProperty("order_priority")]
    public int? OrderPriority { get; set; }

    [JsonProperty("fix_order")]
    public int? FixOrder { get; set; }

    public int? NewsId
    {
        get
        {
            var query = this.Link?.Query;

            if (string.IsNullOrWhiteSpace(query))
                return null;

            foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = segment.Split('=', 2);

                if (!pair[0].Equals("id", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var newsId)
                    ? newsId
                    : null;
            }

            return null;
        }
    }
}
