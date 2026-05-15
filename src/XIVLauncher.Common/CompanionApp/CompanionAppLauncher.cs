using System.ComponentModel;
using System.Diagnostics;
using Serilog;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.CompanionApp;

internal static class CompanionAppLauncher
{
    private static readonly Lazy<string> LazyPowershell = new(GetPowershell);

    private static string Powershell => LazyPowershell.Value;

    public static Process? Start(CompanionAppConfiguration companionApp)
    {
        if (string.IsNullOrWhiteSpace(companionApp.FilePath))
        {
            Log.Error("Companion app path was null");
            return null;
        }

        try
        {
            var fileExtension = Path.GetExtension(companionApp.FilePath);
            var process = fileExtension.ToLowerInvariant() switch
            {
                ".ps1" => StartScript(CreatePowershellStartInfo(companionApp), companionApp),
                ".bat" => StartScript(CreateBatchStartInfo(companionApp),      companionApp),
                _      => StartApplication(companionApp)
            };

            Log.Information("Launched companion app {CompanionAppName}", Path.GetFileNameWithoutExtension(companionApp.FilePath));
            return process;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not launch companion app");
            return null;
        }
    }

    public static void Stop(CompanionAppConfiguration companionApp, Process process)
    {
        try
        {
            if (process.Handle == IntPtr.Zero || process.HasExited)
                return;

            if (!process.CloseMainWindow() || !process.WaitForExit(1000))
                process.Kill();

            process.Close();
        }
        catch (Exception ex)
        {
            Log.Information(ex, "Could not kill companion app process {CompanionAppName}", companionApp.Name);
        }
    }

    private static Process? StartApplication(CompanionAppConfiguration companionApp)
    {
        var process = new Process
        {
            StartInfo =
            {
                FileName         = companionApp.FilePath,
                Arguments        = companionApp.Arguments,
                UseShellExecute  = true,
                WorkingDirectory = Path.GetDirectoryName(companionApp.FilePath),
                WindowStyle      = ProcessWindowStyle.Minimized
            }
        };

        ApplyElevation(process.StartInfo, companionApp.RunAsAdmin);

        try
        {
            process.Start();
            return process;
        }
        catch (Win32Exception ex) when (PlatformHelpers.IsWindowsErrorCancelled(ex))
        {
            return null;
        }
    }

    private static Process? StartScript(ProcessStartInfo startInfo, CompanionAppConfiguration companionApp)
    {
        startInfo.WindowStyle    = ProcessWindowStyle.Hidden;
        startInfo.CreateNoWindow = true;

        ApplyElevation(startInfo, companionApp.RunAsAdmin);

        try
        {
            return Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (PlatformHelpers.IsWindowsErrorCancelled(ex))
        {
            return null;
        }
    }

    private static ProcessStartInfo CreatePowershellStartInfo(CompanionAppConfiguration companionApp) =>
        new()
        {
            FileName         = Powershell,
            WorkingDirectory = Path.GetDirectoryName(companionApp.FilePath),
            Arguments        = $"""-File "{companionApp.FilePath}" {companionApp.Arguments}""",
            UseShellExecute  = false
        };

    private static ProcessStartInfo CreateBatchStartInfo(CompanionAppConfiguration companionApp) =>
        new()
        {
            FileName         = Environment.GetEnvironmentVariable("ComSpec"),
            WorkingDirectory = Path.GetDirectoryName(companionApp.FilePath),
            Arguments        = $"""/C "{companionApp.FilePath}" {companionApp.Arguments}""",
            UseShellExecute  = false
        };

    private static void ApplyElevation(ProcessStartInfo startInfo, bool runAsAdmin)
    {
        if (!runAsAdmin)
            return;

        if (Environment.OSVersion.Version.Major >= 6)
            startInfo.Verb = "runas";
    }

    private static string GetPowershell()
    {
        var result = "powershell.exe";
        var path   = Environment.GetEnvironmentVariable("Path");
        var values = path?.Split(';') ?? [];

        foreach (var directoryPath in values)
        {
            var powershellPath = Path.Combine(directoryPath, "pwsh.exe");
            if (!File.Exists(powershellPath))
                continue;

            result = powershellPath;
            break;
        }

        return result;
    }
}
