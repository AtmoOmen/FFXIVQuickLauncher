using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommandLine;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.Startup.Steps;

public class CommandLineStep : IStartupStep
{
    public string Name  => "命令行解析";
    public int    Order => 15;

    public FileInfo? DalamudRunnerOverride { get; private set; }

    private CommandLineOptions options = new();

    public Task ExecuteAsync(StartupContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var helpWriter = new StringWriter();
            var parser = new Parser
            (config =>
                {
                    config.HelpWriter             = helpWriter;
                    config.IgnoreUnknownArguments = true;
                }
            );
            var result = parser.ParseArguments<CommandLineOptions>(Environment.GetCommandLineArgs());

            if (result.Errors.Any())
                MessageBox.Show(helpWriter.ToString(), "帮助");

            var cmdLine = result.Value ?? new CommandLineOptions();

            if (!string.IsNullOrEmpty(cmdLine.RoamingPath))
                Paths.OverrideRoamingPath(cmdLine.RoamingPath);

            if (!string.IsNullOrEmpty(cmdLine.RunnerOverride))
                DalamudRunnerOverride = new FileInfo(cmdLine.RunnerOverride);

            if (cmdLine.DoGenerateLocalizables)
                GenerateLocalizables();

            options = cmdLine;
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法解析命令行参数, 请反馈此问题\n\n" + ex.Message, "XIVLauncherCN (Soil)", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }

        return Task.CompletedTask;
    }

    public CommandLineOptions GetOptions() => options;

    private static void GenerateLocalizables()
    {
        // 本地化导出功能已禁用
        Environment.Exit(0);
    }
}
