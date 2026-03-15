using System.Text.Json.Serialization;

namespace XIVLauncher.Common.Game.DCTravel;

public sealed class DCTravelGroup
{
    public int    AreaID   { get; set; }
    public string AreaName { get; set; }

    [JsonPropertyName("groupId")]   public int    GroupID   { get; set; }
    [JsonPropertyName("amount")]    public int    Amount    { get; set; }
    [JsonPropertyName("groupName")] public string GroupName { get; set; }
    [JsonPropertyName("queueTime")] public int?   QueueTime { get; set; }
    [JsonPropertyName("groupCode")] public string GroupCode { get; set; }
}
