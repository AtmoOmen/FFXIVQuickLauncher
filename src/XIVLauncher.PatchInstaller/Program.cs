using System;
using System.CommandLine;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;
using XIVLauncher.PatchInstaller.Commands;

namespace XIVLauncher.PatchInstaller;

public static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
                     .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Fatal)
                     .WriteTo.Debug()
                     .MinimumLevel.Verbose()
                     .CreateLogger();

        var rc = new RootCommand();
        rc.Subcommands.Add(CheckIntegrityCommand.Command);
        rc.Subcommands.Add(InstallCommand.Command);
        rc.Subcommands.Add(IndexCreateCommand.Command);
        rc.Subcommands.Add(IndexCreateIntegrityCommand.Command);
        rc.Subcommands.Add(IndexVerifyCommand.Command);
        rc.Subcommands.Add(IndexRepairCommand.Command);
        rc.Subcommands.Add(IndexUpdateCommand.Command);
        rc.Subcommands.Add(IndexRpcCommand.Command);
        rc.Subcommands.Add(IndexRpcTestCommand.Command);
        rc.Subcommands.Add(RpcCommand.Command);
        rc.Subcommands.Add(SdoRpcCommand.Command);

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
