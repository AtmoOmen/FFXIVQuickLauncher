using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Game;

public sealed class FFXIVProcess
(
    Process p
) : IDisposable
{
    public Process UnderlyingProcess { get; } = p;

    public int    ProcessID   => UnderlyingProcess.Id;
    public string ProcessName => UnderlyingProcess.ProcessName;
    public int    ExitCode    => UnderlyingProcess.ExitCode;
    public string DisplayName { get; init; } = $"{p.Id} ({p.StartTime})";
    public bool   HasInjected { get; set; }  = IsDalamudInjected(p);

    public void Dispose() =>
        UnderlyingProcess.Dispose();

    public static bool IsDalamudInjected(Process p) =>
        p.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Contains("Dalamud.dll"));

    public static List<int> GetGameProcessIDs() =>
        GetGameProcesses().Select(p => p.Id).ToList();

    public static List<FFXIVProcess> GetGameProcess() =>
        GetGameProcesses().Select(p => new FFXIVProcess(p)).ToList();

    private static IEnumerable<Process> GetGameProcesses()
    {
        var processes = Process.GetProcesses()
                               .Where(p => p.ProcessName == "ffxiv_dx11")
                               .Where(p => !p.MainWindowTitle.Contains("FINAL FANTASY XIV"));
        if (PlatformHelpers.IsElevated())
            processes = processes.Where(p => !p.HasExited);

        return processes;
    }
}
