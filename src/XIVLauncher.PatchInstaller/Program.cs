using System;
using System.CommandLine;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.PatchInstaller.Commands;
using XIVLauncher.PatchInstaller.Support;

namespace XIVLauncher.PatchInstaller;

public static class Program
{
    private static async Task<int> Main(string[] args)
    {
        PatchInstallerLog.Setup();
        Log.Information("[PatchInstaller] 启动补丁进程, 参数 {Args}", string.Join(' ', args));

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
            Log.Information("[PatchInstaller] 操作完成, 返回码 {ReturnCode}", ret);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PatchInstaller] 操作失败");
        }

        return ret;
    }
}
