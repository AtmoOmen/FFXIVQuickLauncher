using System.Text.Json.Serialization;

namespace XIVLauncher.GamePatchV3;

public sealed class VersionMappingEntry
{
    [JsonPropertyName("V")]
    public string V { get; set; } = string.Empty;

    [JsonPropertyName("View")]
    public string View { get; set; } = string.Empty;
}
