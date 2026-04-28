using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using XIVLauncher.Common.Patching;
using XIVLauncher.Common.Patching.Rpc.Implementations;
using XIVLauncher.PatchInstaller.Support;

namespace XIVLauncher.PatchInstaller.Commands;

public class RpcCommand
{
    public static readonly Command COMMAND = new("rpc") { Hidden = true };

    private static readonly Argument<string> ChannelNameArgument = new("channel-name");

    private readonly string channelName;

    static RpcCommand()
    {
        COMMAND.Arguments.Add(ChannelNameArgument);
        COMMAND.SetAction(parseResult => new RpcCommand(parseResult).Handle());
    }

    private RpcCommand(ParseResult parseResult) =>
        channelName = parseResult.GetValue(ChannelNameArgument)!;

    private Task<int> Handle()
    {
        PatchInstallerLog.Setup();
        Log.Information("[PatchInstaller] 启动 ZiPatch RPC, 通道 {ChannelName}", channelName);

        try
        {
            var installer = new RemotePatchInstaller(new SharedMemoryRpc(channelName));
            installer.Start();

            while (true)
            {
                if (Process.GetProcesses().All(x => x.ProcessName != "XIVLauncherCN") && !installer.HasQueuedInstalls || installer.IsDone)
                {
                    Environment.Exit(0);
                    return Task.FromResult(0); // does not run
                }

                Thread.Sleep(1000);

                if (installer.IsFailed)
                {
                    Log.Information("[PatchInstaller] ZiPatch RPC 因安装失败退出");
                    Environment.Exit(-1);
                    return Task.FromResult(-1); // does not run
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Patcher init failed.\n\n" + ex, "XIVLauncherCN", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }
}
