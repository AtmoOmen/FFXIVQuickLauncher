using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using Serilog.Events;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Support;
using XIVLauncher.Support;
using DateTimeOffset = System.DateTimeOffset;

namespace XIVLauncher.Startup.Steps;

public class LoggingStep : IStartupStep
{
    public string Name  => "日志初始化";
    public int    Order => 20;

    public Task ExecuteAsync(StartupContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            LogInit.Setup
            (
                Path.Combine(Paths.RoamingPath, "output.log"),
                Environment.GetCommandLineArgs()
            );

            Log.Information("========================================================");
            Log.Information("启动会话 (v{Version} - {Hash})", AppUtil.GetAssemblyVersion(), AppUtil.GetGitHash());

            SerilogEventSink.Instance.LogLine += OnSerilogLogLine;
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法设置日志记录, 请反馈此问题\n\n" + ex.Message, "XIVLauncherCN (Soil)", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }

        return Task.CompletedTask;
    }

    private static void OnSerilogLogLine
    (
        object?                                                                                   sender,
        (string Line, LogEventLevel Level, DateTimeOffset TimeStamp, System.Exception? Exception) e
    )
    {
        if (e.Exception == null)
            return;

        Troubleshooting.LogException(e.Exception, e.Line);
    }
}
