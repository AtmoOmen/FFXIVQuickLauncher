using System.CommandLine;
using System.CommandLine.Parsing;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Patching.IndexedZiPatch;
using XIVLauncher.PatchInstaller.Support;

namespace XIVLauncher.PatchInstaller.Commands;

public class IndexRpcCommand
{
    public static readonly Command COMMAND = new("index-rpc") { Hidden = true };

    private static readonly Argument<int> MonitorProcessIDArgument = new("process-id");

    private static readonly Argument<string> ChannelNameArgument = new("channel-name");

    private readonly int    monitorProcessId;
    private readonly string channelName;

    static IndexRpcCommand()
    {
        COMMAND.Arguments.Add(MonitorProcessIDArgument);
        COMMAND.Arguments.Add(ChannelNameArgument);
        COMMAND.SetAction(parseResult => new IndexRpcCommand(parseResult).Handle());
    }

    private IndexRpcCommand(ParseResult parseResult)
    {
        monitorProcessId = parseResult.GetValue(MonitorProcessIDArgument);
        channelName      = parseResult.GetValue(ChannelNameArgument)!;
    }

    private Task<int> Handle()
    {
        PatchInstallerLog.Setup();
        Log.Information("[PatchInstaller] 启动索引 RPC, 父进程 {ProcessId}, 通道 {ChannelName}", monitorProcessId, channelName);

        new IndexedZiPatchIndexRemoteInstaller.WorkerSubprocessBody(monitorProcessId, channelName).RunToDisposeSelf();
        Log.Information("[PatchInstaller] 索引 RPC 已退出");
        return Task.FromResult(0);
    }
}
