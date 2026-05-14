namespace XIVLauncher.GamePatchV3;

public sealed class IntegrityCheckResult
{
    public Dictionary<string, string> Hashes          { get; set; } = [];
    public Dictionary<string, ulong>  Sizes           { get; set; } = [];
    public string                     GameVersion     { get; set; } = string.Empty;
    public string                     LastGameVersion { get; set; } = string.Empty;
    public string                     BaseUrl         { get; set; } = string.Empty;
    public string                     DataVersion     { get; set; } = string.Empty;
    public string                     AppId           { get; set; } = string.Empty;
}
