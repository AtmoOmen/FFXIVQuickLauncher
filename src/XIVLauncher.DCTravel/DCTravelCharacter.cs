using System.Text.Json.Serialization;

namespace XIVLauncher.DCTravel;

public class DCTravelCharacter
{
    [JsonPropertyName("roleId")]
    public string ContentID { get; set; } = null!;

    [JsonPropertyName("roleName")]
    public string Name { get; set; } = null!;

    public int AreaID  { get; set; }
    public int GroupID { get; set; }

    [JsonIgnore]
    public string ServerName { get; set; } = string.Empty;

    public string ToQueryString() =>
        $$"""{"roleId":"{{ContentID}}","roleName":"{{Name}}","key":0}""";
}
