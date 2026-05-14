using System.IO;
using XIVLauncher.Common;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.PlatformAbstractions;

public class CommonSettings : ISettings
{
    public static CommonSettings Instance
    {
        get
        {
            instance ??= new CommonSettings();
            return instance;
        }
    }

    public         ClientLanguage?    ClientLanguage          => Common.ClientLanguage.ChineseSimplified;
    public         bool?              KeepPatches             => App.Settings.KeepPatches;
    public         DirectoryInfo      PatchPath               => App.Settings.PatchPath;
    public         DirectoryInfo      GamePath                => App.Settings.GamePath;
    public         long               SpeedLimitBytes         => App.Settings.SpeedLimitBytes;
    public         int                DalamudInjectionDelayMs => (int)App.Settings.DalamudInjectionDelayMS;
    private static CommonSettings     instance;
}
