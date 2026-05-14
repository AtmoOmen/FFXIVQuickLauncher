using Serilog;
using Serilog.Events;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.ArgReader;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ConfigureLogger();

        if (args.Length != 1)
        {
            Log.Error("[ArgReader] 参数错误");
            return -1;
        }

        try
        {
            await using var app = new ArgReaderApp(args[0]);
            await app.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[ArgReader] 致命错误");
            return -1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ConfigureLogger()
    {
        Log.Logger = new LoggerConfiguration()
                     .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Fatal)
                     .WriteTo.File(Path.Combine(Paths.RoamingPath, "argReader.log"))
                     .WriteTo.Debug()
                     .MinimumLevel.Verbose()
                     .CreateLogger();
    }
}
