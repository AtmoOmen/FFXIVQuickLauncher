namespace XIVLauncher.GamePatchV3.Models;

public sealed class RemoteVersion
{
    public string                   BaseUrl       { get; set; } = string.Empty;
    public string                   BackupBaseUrl { get; set; } = string.Empty;
    public List<GameVersionArea>    Areas         { get; set; } = [];
    public List<GameVersionPackage> Packages      { get; set; } = [];
}
