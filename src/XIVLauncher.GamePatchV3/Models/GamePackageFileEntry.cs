namespace XIVLauncher.GamePatchV3.Models;

public sealed class GamePackageFileEntry
{
    public string Url  { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Md5  { get; set; } = string.Empty;
    public long   Size { get; set; }
}
