using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Patching.SdoFileDownload;

namespace XIVLauncher.PatchInstaller.Commands;

public class SdoRpcCommand
{
    public static readonly Command Command = new("sdo-rpc") { IsHidden = true };

    private static readonly Argument<int> MonitorProcessIDArgument = new("process-id");

    private static readonly Argument<string> ChannelNameArgument = new("channel-name");

    private readonly int    monitorProcessId;
    private readonly string channelName;

    static SdoRpcCommand()
    {
        Command.AddArgument(MonitorProcessIDArgument);
        Command.AddArgument(ChannelNameArgument);
        Command.SetHandler(x => new SdoRpcCommand(x.ParseResult).Handle());
    }

    private SdoRpcCommand(ParseResult parseResult)
    {
        monitorProcessId = parseResult.GetValueForArgument(MonitorProcessIDArgument);
        channelName      = parseResult.GetValueForArgument(ChannelNameArgument);
    }

    private Task<int> Handle()
    {
        Log.Logger = new LoggerConfiguration()
                     .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Fatal)
                     .WriteTo.File(Path.Combine(Paths.RoamingPath, "patcher.log"))
                     .WriteTo.Debug()
                     .MinimumLevel.Verbose()
                     .CreateLogger();

        new SdoFileDownloadRemoteInstaller.WorkerSubprocessBody(monitorProcessId, channelName).RunToDisposeSelf();
        return Task.FromResult(0);
    }
}
