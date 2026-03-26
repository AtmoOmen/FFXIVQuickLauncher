namespace XIVLauncher.Windows.Services;

internal interface IShortcutService
{
    void CreateShortcut(string directory, string shortcutName, string targetPath, string? description = null, string? iconLocation = null, string? arguments = null);

    string GetShortcutTargetFile(string path);
}
