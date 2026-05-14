namespace XIVLauncher.GamePatchV3;

public sealed class GameUpdatePlan
{
    public string                   BaseUrl            { get; set; } = string.Empty;
    public string                   BackupBaseUrl      { get; set; } = string.Empty;
    public string                   CurrentGameVersion { get; set; } = string.Empty;
    public string                   CurrentDataVersion { get; set; } = string.Empty;
    public string                   CurrentViewVersion { get; set; } = string.Empty;
    public string                   TargetGameVersion  { get; set; } = string.Empty;
    public string                   TargetDataVersion  { get; set; } = string.Empty;
    public string                   TargetViewVersion  { get; set; } = string.Empty;
    public List<GameVersionPackage> Packages           { get; set; } = [];
}
