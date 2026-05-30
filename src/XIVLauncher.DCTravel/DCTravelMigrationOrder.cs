using System.Text.Json.Serialization;

namespace XIVLauncher.DCTravel;

public class DCTravelMigrationOrder
{
    [JsonPropertyName("orderId")]
    public string OrderID { get; set; } = null!;

    [JsonPropertyName("roleId")]
    public string ContentID { get; set; } = null!;

    [JsonPropertyName("groupId")]
    public int GroupID { get; set; }

    [JsonPropertyName("groupCode")]
    public string GroupCode { get; set; } = null!;

    [JsonPropertyName("groupName")]
    public string GroupName { get; set; } = null!;

    [JsonPropertyName("createTime")]
    public string CreateTime { get; set; } = null!;

    [JsonPropertyName("travelStatus")]
    public int TravelStatus { get; set; }
}
