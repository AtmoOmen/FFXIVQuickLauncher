using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using XIVLauncher.Common.Windows;
using XIVLauncher.GamePatchV3;
using XIVLauncher.PlatformAbstractions;
using XIVLauncher.Windows;

namespace XIVLauncher;

public static class AppUtil
{
    extension(byte[] data)
    {
        public BitmapImage? ToBitmapImage()
        {
            if (data is not { Length: > 0 }) return null;

            var bitmapImage = new BitmapImage();

            using var stream = new MemoryStream(data);

            stream.Seek(0, SeekOrigin.Begin);
            bitmapImage.BeginInit();
            bitmapImage.CacheOption  = BitmapCacheOption.OnLoad;
            bitmapImage.StreamSource = stream;
            bitmapImage.EndInit();
            bitmapImage.Freeze();

            return bitmapImage;
        }
    }

    public static string? GetGitHash()
    {
        var asm   = typeof(AppUtil).Assembly;
        var attrs = asm.GetCustomAttributes<AssemblyMetadataAttribute>();
        return attrs.FirstOrDefault(a => a.Key == "GitHash")?.Value;
    }

    public static string? GetBuildOrigin()
    {
        var asm   = typeof(AppUtil).Assembly;
        var attrs = asm.GetCustomAttributes<AssemblyMetadataAttribute>();
        return attrs.FirstOrDefault(a => a.Key == "BuildOrigin")?.Value;
    }

    public static string? GetAssemblyVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fvi      = FileVersionInfo.GetVersionInfo(assembly.Location);
        return fvi.FileVersion;
    }

    public static string GetFromResources(string resourceName)
    {
        var asm = typeof(AppUtil).Assembly;

        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null)
            return string.Empty;

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    public static bool TryYellOnGameFilesBeingOpen(Window parentWindow, Func<int, string> messageGenerator)
    {
        try
        {
            var gamePath = Path.Combine(CommonSettings.Instance.GamePath.FullName, "game");

            while (true)
            {
                using var restartManager = new WindowsRestartManager();
                restartManager.Register(GameRepairer.GetRelevantFiles(gamePath));

                var programs = restartManager.GetInterferingProcesses(out _).ToArray();
                if (programs.Length == 0)
                    return true;

                var description = string.Join
                (
                    Environment.NewLine,
                    programs.Select
                    (x =>
                        {
                            var process = x.Process;
                            if (process == null)
                                return $"{x.AppName} ({x.UniqueProcess.dwProcessId})";

                            var pid     = x.UniqueProcess.dwProcessId;
                            var exeName = process.MainModule?.ModuleName ?? "??";
                            var title   = process.MainWindowTitle;

                            return string.IsNullOrWhiteSpace(title) || title == x.AppName
                                       ? $"{x.AppName} ({pid}: {exeName})"
                                       : $"{x.AppName} ({pid}: {exeName}, \"{title}\")";
                        }
                    )
                );

                var result = CustomMessageBox.Builder
                                             .NewFrom(messageGenerator(programs.Length))
                                             .WithDescription(description)
                                             .WithImage(MessageBoxImage.Information)
                                             .WithButtons(MessageBoxButton.YesNoCancel)
                                             .WithYesButtonText("刷新")
                                             .WithNoButtonText("忽略")
                                             .WithDefaultResult(MessageBoxResult.Yes)
                                             .WithParentWindow(parentWindow)
                                             .Show();

                if (result == MessageBoxResult.No)
                    return true;

                if (result == MessageBoxResult.Cancel)
                    return false;
            }
        }
        catch
        {
            return true;
        }
    }
}
