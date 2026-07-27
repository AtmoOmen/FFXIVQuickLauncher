using System.ComponentModel;
using System.Diagnostics;

namespace XIVLauncher.Common.Game;

public sealed class FFXIVProcess : IDisposable, INotifyPropertyChanged
{
    public FFXIVProcess(Process process)
    {
        UnderlyingProcess = process;
        ProcessID         = process.Id;
        ProcessName       = process.ProcessName;
        HasInjected       = TryIsDalamudInjected(process);
        DisplayName       = $"{process.Id} ({process.StartTime})";
    }

    public Process UnderlyingProcess { get; }

    public int    ProcessID   { get; }
    public string ProcessName { get; }
    public int    ExitCode    => UnderlyingProcess.ExitCode;
    public string DisplayName { get; }

    public bool HasInjected
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasInjected)));
        }
    }

    public void Dispose() =>
        UnderlyingProcess.Dispose();

    public static bool IsDalamudInjected(Process process) =>
        process.Modules.Cast<ProcessModule>().Any(module => string.Equals(module.ModuleName, "Dalamud.dll", StringComparison.OrdinalIgnoreCase));

    public static List<int> GetGameProcessIDs()
    {
        var processes = GetGameProcesses();

        try
        {
            return processes.Select(process => process.Id).ToList();
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public static List<FFXIVProcess> GetGameProcess()
    {
        var processes    = GetGameProcesses();
        var gameProcesses = new List<FFXIVProcess>(processes.Count);

        foreach (var process in processes)
        {
            try
            {
                gameProcesses.Add(new FFXIVProcess(process));
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
            }
        }

        return gameProcesses;
    }

    private static List<Process> GetGameProcesses()
    {
        var gameProcesses = new List<Process>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (string.Equals(process.ProcessName, "ffxiv_dx11", StringComparison.OrdinalIgnoreCase) && !process.HasExited)
                {
                    gameProcesses.Add(process);
                    continue;
                }
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
                continue;
            }

            process.Dispose();
        }

        return gameProcesses;
    }

    private static bool TryIsDalamudInjected(Process process)
    {
        try
        {
            return IsDalamudInjected(process);
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
