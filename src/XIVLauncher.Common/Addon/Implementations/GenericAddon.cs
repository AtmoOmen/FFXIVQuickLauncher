using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Serilog;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Addon.Implementations;

public class GenericAddon : IRunnableAddon, INotifyAddonAfterClose
{
    public string Name =>
        string.IsNullOrEmpty(Path)
            ? "无效程序"
            : $"{(IsApp ? "程序" : string.Empty)}: {System.IO.Path.GetFileNameWithoutExtension(Path)}";

    public string Path;
    public string CommandLine;
    public bool   RunAsAdmin;
    public bool   RunOnClose;
    public bool   KillAfterClose;

    private bool IsApp =>
        !string.IsNullOrEmpty(Path) && System.IO.Path.GetExtension(Path).ToLower() == ".exe";

    private static readonly Lazy<string> LazyPowershell = new(GetPowershell);

    private static string  Powershell => LazyPowershell.Value;
    private        Process _addonProcess;

    public void Run() =>
        Run(false);

    public void GameClosed()
    {
        if (!RunAsAdmin && !RunOnClose && KillAfterClose)
        {
            try
            {
                if (_addonProcess == null)
                    return;

                if (_addonProcess.Handle == IntPtr.Zero)
                    return;

                if (!_addonProcess.HasExited)
                {
                    if (!_addonProcess.CloseMainWindow() || !_addonProcess.WaitForExit(1000))
                        _addonProcess.Kill();

                    _addonProcess.Close();
                }
            }
            catch (Exception ex)
            {
                Log.Information(ex, "Could not kill addon process.");
            }
        }

        if (RunOnClose)
            Run(true);
    }

    private static string GetPowershell()
    {
        var result = "powershell.exe";

        var path   = Environment.GetEnvironmentVariable("Path");
        var values = path?.Split(';') ?? Array.Empty<string>();

        foreach (var dir in values)
        {
            var powershell = System.IO.Path.Combine(dir, "pwsh.exe");

            if (File.Exists(powershell))
            {
                result = powershell;
                break;
            }
        }

        return result;
    }

    void IAddon.Setup(int gamePid)
    {
    }

    private void Run(bool gameClosed)
    {
        if (string.IsNullOrEmpty(Path))
        {
            Log.Error("Generic addon path was null.");
            return;
        }

        if (RunOnClose && !gameClosed)
            return; // This Addon only runs when the game is closed.

        try
        {
            var ext = System.IO.Path.GetExtension(Path).ToLower();

            switch (ext)
            {
                case ".ps1":
                    RunPowershell();
                    break;

                case ".bat":
                    RunBatch();
                    break;

                default:
                    RunApp();
                    break;
            }

            Log.Information("Launched addon {0}.", System.IO.Path.GetFileNameWithoutExtension(Path));
        }
        catch (Exception e)
        {
            Log.Error(e, "Could not launch generic addon.");
        }
    }

    private void RunApp()
    {
        _addonProcess = new Process
        {
            StartInfo =
            {
                FileName         = Path,
                Arguments        = CommandLine,
                UseShellExecute  = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(Path)
            }
        };

        if (RunAsAdmin)
            // Vista or higher check
            // https://stackoverflow.com/a/2532775
        {
            if (Environment.OSVersion.Version.Major >= 6)
                _addonProcess.StartInfo.Verb = "runas";
        }

        _addonProcess.StartInfo.WindowStyle = ProcessWindowStyle.Minimized;

        try
        {
            _addonProcess.Start();
        }
        catch (Win32Exception ex) when (PlatformHelpers.IsWindowsErrorCancelled(ex))
        {
        }
    }

    private void RunPowershell()
    {
        var ps = new ProcessStartInfo
        {
            FileName         = Powershell,
            WorkingDirectory = System.IO.Path.GetDirectoryName(Path),
            Arguments        = $@"-File ""{Path}"" {CommandLine}",
            UseShellExecute  = false
        };

        RunScript(ps);
    }

    private void RunBatch()
    {
        var ps = new ProcessStartInfo
        {
            FileName         = Environment.GetEnvironmentVariable("ComSpec"),
            WorkingDirectory = System.IO.Path.GetDirectoryName(Path),
            Arguments        = $@"/C ""{Path}"" {CommandLine}",
            UseShellExecute  = false
        };

        RunScript(ps);
    }

    private void RunScript(ProcessStartInfo ps)
    {
        ps.WindowStyle    = ProcessWindowStyle.Hidden;
        ps.CreateNoWindow = true;

        if (RunAsAdmin)
            // Vista or higher check
            // https://stackoverflow.com/a/2532775
        {
            if (Environment.OSVersion.Version.Major >= 6)
                ps.Verb = "runas";
        }

        try
        {
            _addonProcess = Process.Start(ps);
            Log.Information("Launched addon {0}.", System.IO.Path.GetFileNameWithoutExtension(Path));
        }
        catch (Win32Exception exc)
        {
            // If the user didn't cause this manually by dismissing the UAC prompt, we throw it
            if (!PlatformHelpers.IsWindowsErrorCancelled(exc))
                throw;
        }
    }
}
