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

    [JsonPropertyName("roleName")]
    public string RoleName { get; set; } = string.Empty;

    [JsonPropertyName("sourceAreaName")]
    public string SourceAreaName { get; set; } = string.Empty;

    [JsonPropertyName("sourceGroupName")]
    public string SourceGroupName { get; set; } = string.Empty;

    [JsonPropertyName("targetAreaName")]
    public string TargetAreaName { get; set; } = string.Empty;

    [JsonPropertyName("targetGroupName")]
    public string TargetGroupName { get; set; } = string.Empty;
}
