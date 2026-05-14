namespace XIVLauncher.Common.PlatformAbstractions;

public interface ISettings
{
    ClientLanguage? ClientLanguage          { get; }
    bool?           KeepPatches             { get; }
    DirectoryInfo   PatchPath               { get; }
    DirectoryInfo   GamePath                { get; }
    long            SpeedLimitBytes         { get; }
    int             DalamudInjectionDelayMs { get; }
}
