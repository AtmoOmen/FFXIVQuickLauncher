using System.ComponentModel;
using System.Diagnostics;

namespace XIVLauncher.Common.Util;

public static class GameHelpers
{
    public static bool IsValidGamePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var gamePath = new DirectoryInfo(path);
        return File.Exists(Path.Combine(path, "game", "ffxiv_dx11.exe")) && !Repository.Ffxiv.IsBaseVer(gamePath);
    }

    public static bool CanMightNotBeInternationalClient(string path) =>
        Directory.Exists(Path.Combine(path, "sdo")) || File.Exists(Path.Combine(path, "boot", "FFXIV_Boot.exe"));

    public static bool LetChoosePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return true;

        var di = new DirectoryInfo(path);
        return di.Name switch
        {
            "game" or "boot" or "sqpack" => false,
            _                            => true
        };

    }

    public static FileInfo GetOfficialLauncherPath(DirectoryInfo gamePath) => new
        (File.Exists(Path.Combine(gamePath.FullName, "FFXIVBootV3.exe")) ? Path.Combine(gamePath.FullName, "FFXIVBootV3.exe") : Path.Combine(gamePath.FullName, "FFXIVBoot.exe"));

    public static void StartOfficialLauncher(DirectoryInfo gamePath)
    {
        var startInfo = new ProcessStartInfo(GetOfficialLauncherPath(gamePath).FullName)
        {
            WorkingDirectory = gamePath.FullName,
            UseShellExecute  = true
        };

        // Start as admin if needed
        if (!EnvironmentSettings.IsNoRunas && Environment.OSVersion.Version.Major >= 6)
            startInfo.Verb = "runas";

        try
        {
            Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (PlatformHelpers.IsWindowsErrorCancelled(ex))
        {
        }
    }

    public static bool CheckIsGameOpen()
    {
#if DEBUG
        return false;
#endif

        var procs = Process.GetProcesses();

        if (procs.Any(x => x.ProcessName == "ffxiv"))
            return true;

        if (procs.Any(x => x.ProcessName == "ffxiv_dx11"))
            return true;

        if (procs.Any(x => x.ProcessName == "ffxivboot"))
            return true;

        if (procs.Any(x => x.ProcessName == "ffxivlauncher"))
            return true;

        return false;
    }

    public static string ToMangledSeBase64(byte[] input) =>
        Convert.ToBase64String(input)
               .Replace('+', '-')
               .Replace('/', '_')
               .Replace('=', '*');
}
