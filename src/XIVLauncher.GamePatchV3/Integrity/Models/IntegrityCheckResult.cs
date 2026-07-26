namespace XIVLauncher.GamePatchV3.Integrity.Models;

public sealed class IntegrityCheckResult
{
    public Dictionary<string, string> Hashes          { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ulong>  Sizes           { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string                     GameVersion     { get; set; } = string.Empty;
    public string                     LastGameVersion { get; set; } = string.Empty;
    public string                     BaseUrl         { get; set; } = string.Empty;
    public string                     DataVersion     { get; set; } = string.Empty;
    public string                     AppId           { get; set; } = string.Empty;
}
