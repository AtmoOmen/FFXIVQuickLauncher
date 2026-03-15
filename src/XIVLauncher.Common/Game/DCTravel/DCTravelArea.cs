using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XIVLauncher.Common.Game.DCTravel;

public sealed class DCTravelArea
{
    [JsonPropertyName("state")]    public int                 State     { get; set; }
    [JsonPropertyName("areaId")]   public int                 AreaID    { get; set; }
    [JsonPropertyName("areaName")] public string              AreaName  { get; set; }
    [JsonPropertyName("groups")]   public List<DCTravelGroup> GroupList { get; set; }

    public void SetAreaForGroup()
    {
        foreach (var group in GroupList)
        {
            group.AreaID   = AreaID;
            group.AreaName = AreaName;
        }
    }
}
