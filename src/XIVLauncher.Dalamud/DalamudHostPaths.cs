namespace XIVLauncher.Dalamud;

public sealed record DalamudHostPaths
(
    DirectoryInfo AddonDirectory,
    DirectoryInfo RuntimeDirectory,
    DirectoryInfo AssetDirectory,
    DirectoryInfo ConfigDirectory,
    DirectoryInfo LogDirectory,
    DirectoryInfo LauncherDirectory
);
