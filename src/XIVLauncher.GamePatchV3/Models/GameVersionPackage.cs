namespace XIVLauncher.GamePatchV3;

public sealed class GameVersionPackage
{
    public string FileListUrl { get; set; } = string.Empty;
    public int    ForceType   { get; set; }
    public string From        { get; set; } = "0.0.0.0";
    public string Md5         { get; set; } = string.Empty;
    public string Name        { get; set; } = string.Empty;
    public string To          { get; set; } = "0.0.0.0";
    public string VersionView { get; set; } = string.Empty;
}
