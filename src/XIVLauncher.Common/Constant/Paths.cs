using Microsoft.Win32;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Constant;

public static class Paths
{
    public static string ResourcesPath { get; } =
        Path.Join(AppContext.BaseDirectory, "Resources");

    public static string RoamingPath { get; private set; } = 
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN");

    public static void OverrideRoamingPath(string path) =>
        RoamingPath = Environment.ExpandEnvironmentVariables(path);

    public static string ResolveExecutablePath()
    {
        var currentProcessPath = Environment.ProcessPath;
        if (IsExecutableFile(currentProcessPath))
            return currentProcessPath!;

        var launcherExecutablePath = Path.Combine(AppContext.BaseDirectory, "XIVLauncherCN.exe");
        if (IsExecutableFile(launcherExecutablePath))
            return launcherExecutablePath;

        return currentProcessPath ?? launcherExecutablePath;
    }

    private static bool IsExecutableFile(string? filePath) =>
        !string.IsNullOrWhiteSpace(filePath)
        && File.Exists(filePath)
        && string.Equals(Path.GetExtension(filePath), ".exe", StringComparison.OrdinalIgnoreCase);
    
    public static DirectoryInfo ResolvePatchPath(DirectoryInfo? configuredPatchPath, string roamingPath)
    {
        if (configuredPatchPath is { Exists: false })
            configuredPatchPath = null;

        return configuredPatchPath ?? new DirectoryInfo(Path.Combine(roamingPath, "patches"));
    }
    
    public static string GetConfigPath(string prefix = "launcher") => 
        Path.Join(RoamingPath, $"{prefix}ConfigV3.json");

    public static string GetGamePath()
    {
        const string REGISTRY_PATH = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\最终幻想14";
        var          defaultPath   = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"上海数龙科技有限公司\最终幻想XIV");

        try
        {
            var foundPaths = new List<(string Path, SeVersion Version)>();

            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var hklm   = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var subkey = hklm.OpenSubKey(REGISTRY_PATH);

                if (subkey?.GetValue("InstallLocation") is string path
                    && Directory.Exists(path)
                    && GameHelpers.IsValidGamePath(path))
                {
                    var versionText = Repository.Ffxiv.GetVer(new DirectoryInfo(path));
                    foundPaths.Add((path, SeVersion.Parse(versionText)));
                }
            }

            return foundPaths
                   .OrderByDescending(x => x.Version)
                   .Select(x => x.Path)
                   .FirstOrDefault()
                   ?? defaultPath;
        }
        catch
        {
            return defaultPath;
        }
    }
}
