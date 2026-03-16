using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CheapLoc;
using CommandLine;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.Startup.Steps;

public class CommandLineStep : IStartupStep
{
    private App.CommandLineOptions options = new();

    public string Name => "命令行解析";
    public int Order => 30;

    public FileInfo? DalamudRunnerOverride { get; private set; }

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
            var result = parser.ParseArguments<App.CommandLineOptions>(Environment.GetCommandLineArgs());

            if (result.Errors.Any())
                MessageBox.Show(helpWriter.ToString(), "帮助");

            var cmdLine = result.Value ?? new App.CommandLineOptions();

            if (!string.IsNullOrEmpty(cmdLine.RoamingPath))
                Paths.OverrideRoamingPath(cmdLine.RoamingPath);

            if (!string.IsNullOrEmpty(cmdLine.RunnerOverride))
                DalamudRunnerOverride = new FileInfo(cmdLine.RunnerOverride);

            if (cmdLine.NoAutoLogin)
                context.IsDisableAutologin = true;

            if (!string.IsNullOrEmpty(cmdLine.DoGenerateIntegrity))
                GenerateIntegrity(cmdLine.DoGenerateIntegrity);

            if (cmdLine.DoGenerateLocalizables)
                GenerateLocalizables();

            if (cmdLine.InjectMode)
                context.InjectMode = true;

            options = cmdLine;
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法解析命令行参数, 请反馈此问题\n\n" + ex.Message, "XIVLauncherCN (Soil)", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        return Task.CompletedTask;
    }

    public App.CommandLineOptions GetOptions() => options;

    private static void GenerateIntegrity(string path)
    {
        var result            = Common.Game.IntegrityCheck.RunIntegrityCheckAsync(new DirectoryInfo(path), null).GetAwaiter().GetResult();
        var saveIntegrityPath = Path.Combine(Paths.RoamingPath, $"{result.GameVersion}.json");

        File.WriteAllText(saveIntegrityPath, Newtonsoft.Json.JsonConvert.SerializeObject(result));

        MessageBox.Show($"已成功对 {result.Hashes.Count} 个文件进行哈希计算并保存到 {path}", "完成", MessageBoxButton.OK, MessageBoxImage.Asterisk);
        Environment.Exit(0);
    }

    private static void GenerateLocalizables()
    {
        try
        {
            Loc.ExportLocalizable();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString());
        }

        Environment.Exit(0);
    }
}
