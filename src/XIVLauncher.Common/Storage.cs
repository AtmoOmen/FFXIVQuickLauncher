using System;
using System.IO;

namespace XIVLauncher.Common;

public class Storage
{
    public DirectoryInfo Root { get; }

    public Storage(string appName, string? overridePath = null)
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            Root = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName));
        else
            Root = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".{appName}"));

        if (!string.IsNullOrEmpty(overridePath))
            Root = new DirectoryInfo(overridePath);

        if (!Root.Exists)
            Root.Create();
    }

    public FileInfo GetFile(string fileName) =>
        new(Path.Combine(Root.FullName, fileName));

    /// <summary>
    ///     Gets a folder and makes sure that it exists.
    /// </summary>
    /// <param name="folderName"></param>
    /// <returns></returns>
    public DirectoryInfo GetFolder(string folderName)
    {
        var folder = new DirectoryInfo(Path.Combine(Root.FullName, folderName));

        if (!folder.Exists)
            folder.Create();

        return folder;
    }
}
