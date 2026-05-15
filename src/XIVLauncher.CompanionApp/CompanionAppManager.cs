using System.Diagnostics;
using Serilog;

namespace XIVLauncher.CompanionApp;

public sealed class CompanionAppManager
{
    private readonly List<TrackedCompanionAppProcess> trackedProcesses = [];
    private          List<CompanionAppConfiguration>? companionApps;

    public bool IsRunning { get; private set; }

    public void Start(IEnumerable<CompanionAppConfiguration> entries)
    {
        if (IsRunning)
            throw new InvalidOperationException("Companion apps are still running");

        companionApps = entries.ToList();

        foreach (var companionApp in companionApps.Where(entry => entry.LaunchTrigger == CompanionAppLaunchTrigger.GameLaunch))
        {
            var process = CompanionAppLauncher.Start(companionApp);
            if (process == null || !companionApp.StopWhenGameExits || companionApp.RunAsAdmin)
                continue;

            trackedProcesses.Add(new TrackedCompanionAppProcess(companionApp, process));
        }

        IsRunning = true;
    }

    public void Stop()
    {
        Log.Information("Stopping companion apps");

        foreach (var trackedProcess in trackedProcesses)
            CompanionAppLauncher.Stop(trackedProcess.Configuration, trackedProcess.Process);

        trackedProcesses.Clear();

        if (companionApps != null)
        {
            foreach (var companionApp in companionApps.Where(entry => entry.LaunchTrigger == CompanionAppLaunchTrigger.GameExit))
                CompanionAppLauncher.Start(companionApp);
        }

        companionApps = null;
        IsRunning     = false;
    }

    private sealed record TrackedCompanionAppProcess
    (
        CompanionAppConfiguration Configuration,
        Process                   Process
    );
}
