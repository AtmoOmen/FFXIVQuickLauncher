namespace XIVLauncher.GamePatchV3;

public sealed class GamePackageFileList
{
    public string                     BaseUrl       { get; set; } = string.Empty;
    public string                     BackupBaseUrl { get; set; } = string.Empty;
    public List<GamePackageFileEntry> FileList      { get; set; } = [];
}
