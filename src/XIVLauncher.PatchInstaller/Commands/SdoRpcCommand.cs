using System.CommandLine;
using System.CommandLine.Parsing;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Patching.SdoFileDownload;
using XIVLauncher.PatchInstaller.Support;

namespace XIVLauncher.PatchInstaller.Commands;

public class SdoRpcCommand
{
    public static readonly Command COMMAND = new("sdo-rpc") { Hidden = true };

    private static readonly Argument<int> MonitorProcessIDArgument = new("process-id");

    private static readonly Argument<string> ChannelNameArgument = new("channel-name");

    private readonly int    monitorProcessId;
    private readonly string channelName;

    static SdoRpcCommand()
    {
        COMMAND.Arguments.Add(MonitorProcessIDArgument);
        COMMAND.Arguments.Add(ChannelNameArgument);
        COMMAND.SetAction(parseResult => new SdoRpcCommand(parseResult).Handle());
    }

    private SdoRpcCommand(ParseResult parseResult)
    {
        monitorProcessId = parseResult.GetValue(MonitorProcessIDArgument);
        channelName      = parseResult.GetValue(ChannelNameArgument)!;
    }

    private Task<int> Handle()
    {
        PatchInstallerLog.Setup();
        Log.Information("[PatchInstaller] 启动 SDO RPC, 父进程 {ProcessId}, 通道 {ChannelName}", monitorProcessId, channelName);

        new SdoFileDownloadRemoteInstaller.WorkerSubprocessBody(monitorProcessId, channelName).RunToDisposeSelf();
        Log.Information("[PatchInstaller] SDO RPC 已退出");
        return Task.FromResult(0);
    }
}
