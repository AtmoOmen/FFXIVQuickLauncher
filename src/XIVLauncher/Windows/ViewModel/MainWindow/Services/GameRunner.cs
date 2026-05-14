using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Dalamud;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Services;

public class GameRunner
(
    DalamudLauncher dalamudLauncher,
    bool            dalamudOk,
    DirectoryInfo   dotnetRuntimePath
) : IGameRunner
{
    public Process Start(string path, string workingDirectory, string arguments, IDictionary<string, string> environment, DPIAwareness dpiAwareness)
    {
        Log.Information($"Game Exe:{path}");

        if (dalamudOk)
        {
            var compat = "RunAsInvoker ";
            compat += dpiAwareness switch
            {
                DPIAwareness.Aware   => "HighDPIAware",
                DPIAwareness.Unaware => "DPIUnaware",
                _                    => throw new ArgumentOutOfRangeException()
            };
            environment.Add("__COMPAT_LAYER", compat);

            var prevDalamudRuntime = Environment.GetEnvironmentVariable("DALAMUD_RUNTIME");
            if (string.IsNullOrWhiteSpace(prevDalamudRuntime))
                environment.Add("DALAMUD_RUNTIME", dotnetRuntimePath.FullName);

            var prevDotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (string.IsNullOrWhiteSpace(prevDotnetRoot))
                environment.Add("DOTNET_ROOT", dotnetRuntimePath.FullName);

            var prevDotnetLookup = Environment.GetEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP");
            if (string.IsNullOrWhiteSpace(prevDotnetLookup))
                environment.Add("DOTNET_MULTILEVEL_LOOKUP", "0");

            return dalamudLauncher.Run(new FileInfo(path), arguments, environment);
        }

        return NativeAclFix.LaunchGame
        (
            workingDirectory,
            path,
            arguments,
            environment,
            dpiAwareness,
            process =>
            {
                var argFix = new GameArgumentInterop.Fixer(process);
                argFix.Fix();
            }
        );
    }
}
