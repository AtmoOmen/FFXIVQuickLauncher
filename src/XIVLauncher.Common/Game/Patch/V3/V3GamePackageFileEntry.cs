namespace XIVLauncher.Common.Game.Patch.V3;

public sealed class V3GamePackageFileEntry
{
    public string Url  { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Md5  { get; set; } = string.Empty;
    public long   Size { get; set; }
}
