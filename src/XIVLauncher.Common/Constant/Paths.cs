using System;
using System.IO;

namespace XIVLauncher.Common.Constant;

public static class Paths
{
    public static string ResourcesPath { get; } =
        Path.Join(AppContext.BaseDirectory, "Resources");

    public static string RoamingPath { get; private set; } = 
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN");

    public static void OverrideRoamingPath(string path) =>
        RoamingPath = Environment.ExpandEnvironmentVariables(path);

    public static DirectoryInfo ResolvePatchPath(DirectoryInfo? configuredPatchPath, string roamingPath)
    {
        if (configuredPatchPath is { Exists: false })
            configuredPatchPath = null;

        return configuredPatchPath ?? new DirectoryInfo(Path.Combine(roamingPath, "patches"));
    }
    
    public static string GetConfigPath(string prefix = "launcher") => 
        Path.Join(RoamingPath, $"{prefix}ConfigV3.json");
}
