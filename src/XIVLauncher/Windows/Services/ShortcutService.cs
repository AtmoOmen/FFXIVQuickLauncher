using System.IO;

namespace XIVLauncher.Windows.Services;

internal sealed class ShortcutService : IShortcutService
{
    public void CreateShortcut
    (
        string  directory,
        string  shortcutName,
        string  targetPath,
        string? description  = null,
        string? iconLocation = null,
        string? arguments    = null
    )
    {
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var     shortcutPath = Path.Combine(directory, $"{shortcutName}.lnk");
        var     shellType    = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("无法创建快捷方式 Shell 对象");
        dynamic shell        = Activator.CreateInstance(shellType)     ?? throw new InvalidOperationException("无法创建快捷方式 Shell 实例");
        var     shortcut     = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath       = targetPath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
        shortcut.WindowStyle      = 1;
        shortcut.Description      = description;
        shortcut.IconLocation     = string.IsNullOrWhiteSpace(iconLocation) ? targetPath : iconLocation;

        if (!string.IsNullOrWhiteSpace(arguments))
            shortcut.Arguments = arguments;

        shortcut.Save();
    }

    public string GetShortcutTargetFile(string path)
    {
        var     shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("无法读取快捷方式");
        dynamic shell     = Activator.CreateInstance(shellType)     ?? throw new InvalidOperationException("无法读取快捷方式");
        var     shortcut  = shell.CreateShortcut(path);
        return shortcut.TargetPath;
    }
}
