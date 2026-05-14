namespace XIVLauncher.Common.PlatformAbstractions;

public interface ISettings
{
    bool?           KeepPatches             { get; }
    DirectoryInfo   PatchPath               { get; }
    DirectoryInfo   GamePath                { get; }
    long            SpeedLimitBytes         { get; }
    int             DalamudInjectionDelayMs { get; }
}
