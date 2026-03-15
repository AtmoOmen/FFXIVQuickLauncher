using System;
using System.Diagnostics;
using System.Linq;

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
    public bool   HasInjected { get; set; }  = DetectDalamud(p);

    #region Disposal

    public void Dispose() =>
        UnderlyingProcess.Dispose();

    #endregion

    public static bool DetectDalamud(Process p) =>
        p.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Contains("Dalamud.dll"));
}
