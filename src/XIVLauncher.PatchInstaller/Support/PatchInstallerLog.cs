using System.IO;
using Serilog;
using Serilog.Events;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.PatchInstaller.Support;

public static class PatchInstallerLog
{
    public static void Setup()
    {
        Log.Logger = new LoggerConfiguration()
                     .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Fatal)
                     .WriteTo.File(Path.Combine(Paths.RoamingPath, "patcher.log"), shared: true)
                     .WriteTo.File(Path.Combine(Paths.RoamingPath, "output.log"), shared: true)
                     .WriteTo.Debug()
                     .MinimumLevel.Verbose()
                     .CreateLogger();
    }
}
