using System.Text.Json.Serialization;

namespace XIVLauncher.Common.Game.DCTravel;

public class DCTravelCharacter
{
    [JsonPropertyName("roleId")]
    public string ContentID { get; set; } = null!;

    [JsonPropertyName("roleName")]
    public string Name { get; set; } = null!;

    public int AreaID  { get; set; }
    public int GroupID { get; set; }

    public string ToQueryString() =>
        $$"""{"roleId":"{{ContentID}}","roleName":"{{Name}}","key":0}""";
}
