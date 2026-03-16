using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Config.Net;
using Serilog;
using Serilog.Events;
using XIVLauncher.Accounts;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Support;
using XIVLauncher.Settings;
using XIVLauncher.Settings.Parsers;
using XIVLauncher.Xaml;

namespace XIVLauncher.Startup.Steps;

public class SettingsStep : IStartupStep
{
    private readonly CommandLineStep commandLineStep;

    public SettingsStep(CommandLineStep commandLineStep)
    {
        this.commandLineStep = commandLineStep;
    }

    public string Name => "设置初始化";
    public int Order => 40;

    public Task ExecuteAsync(StartupContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            SetupSettings(context);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设置已损坏, 正在重置");
            File.Delete(Paths.GetConfigPath());
            SetupSettings(context);
        }

        context.AccountManager = new AccountManager(context.Settings);
        return Task.CompletedTask;
    }

    private void SetupSettings(StartupContext context)
    {
        context.Settings = new ConfigurationBuilder<ILauncherSettingsV3>()
                   .UseCommandLineArgs()
                   .UseJsonFile(Paths.GetConfigPath())
                   .UseTypeParser(new DirectoryInfoParser())
                   .UseTypeParser(new AddonListParser())
                   .UseTypeParser(new CommonJsonParser<PreserveWindowPosition.WindowPlacement>())
                   .Build();

        MachineCode.IsDynamicDeviceId = context.Settings.DynamicDeviceId;

        if (context.Settings.EnableVerboseLog.GetValueOrDefault(false))
            LogInit.LevelSwitch.MinimumLevel = LogEventLevel.Verbose;
        context.Settings.EnableVerboseLog = false;
        Log.Information("当前日志级别: {LevelSwitchMinimumLevel}", LogInit.LevelSwitch.MinimumLevel);

        try
        {
            var cmdLine = commandLineStep.GetOptions();

            if (!string.IsNullOrEmpty(cmdLine.AccountName))
            {
                context.Settings.CurrentAccountId = cmdLine.AccountName;
                Log.Verbose("账号覆盖: '{0}'", cmdLine.AccountName);
            }

            if (cmdLine.ClientLanguage != null)
                context.Settings.Language = ClientLanguage.ChineseSimplified;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法应用命令行设置覆盖");
        }
    }
}
