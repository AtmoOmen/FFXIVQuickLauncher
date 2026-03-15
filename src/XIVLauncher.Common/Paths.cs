using System;
using System.IO;

namespace XIVLauncher.Common;

public static class Paths
{
    public static string ResourcesPath =>
        Path.Join(AppContext.BaseDirectory, "Resources");

    public static string RoamingPath { get; private set; }

    static Paths() =>
        RoamingPath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN");

    public static void OverrideRoamingPath(string path) =>
        RoamingPath = Environment.ExpandEnvironmentVariables(path);

    public static DirectoryInfo ResolvePatchPath(DirectoryInfo? configuredPatchPath, string roamingPath)
    {
        if (configuredPatchPath is { Exists: false })
            configuredPatchPath = null;

        return configuredPatchPath ?? new DirectoryInfo(Path.Combine(roamingPath, "patches"));
    }
}
