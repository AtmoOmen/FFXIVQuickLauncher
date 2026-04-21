using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;
using XIVLauncher.Common.Constant;
using XIVLauncher.PatchInstaller.Commands;

namespace XIVLauncher.PatchInstaller;

public static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
                     .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Fatal)
                     .WriteTo.File(Path.Combine(Paths.RoamingPath, "patcher.log"))
                     .WriteTo.Debug()
                     .MinimumLevel.Verbose()
                     .CreateLogger();

        var rc = new RootCommand();
        rc.Subcommands.Add(CheckIntegrityCommand.COMMAND);
        rc.Subcommands.Add(InstallCommand.COMMAND);
        rc.Subcommands.Add(IndexCreateCommand.COMMAND);
        rc.Subcommands.Add(IndexCreateIntegrityCommand.COMMAND);
        rc.Subcommands.Add(IndexVerifyCommand.COMMAND);
        rc.Subcommands.Add(IndexRepairCommand.COMMAND);
        rc.Subcommands.Add(IndexRpcCommand.COMMAND);
        rc.Subcommands.Add(RpcCommand.COMMAND);
        rc.Subcommands.Add(SdoRpcCommand.COMMAND);

        var ret = -1;

        try
        {
            ret = await rc.Parse(args).InvokeAsync();
            Log.Information("Operation complete.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Operation failed.");
        }

        return ret;
    }
}
