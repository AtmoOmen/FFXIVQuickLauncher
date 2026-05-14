namespace XIVLauncher.GamePatchV3.Models;

public sealed class GamePackageFileList
{
    public string                     BaseUrl       { get; set; } = string.Empty;
    public string                     BackupBaseUrl { get; set; } = string.Empty;
    public List<GamePackageFileEntry> FileList      { get; set; } = [];
}
