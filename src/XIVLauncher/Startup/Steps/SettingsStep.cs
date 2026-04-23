using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;
using XIVLauncher.Accounts;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Support;
using XIVLauncher.Settings;
using XIVLauncher.Xaml;

namespace XIVLauncher.Startup.Steps;

public class SettingsStep
(
    CommandLineStep commandLineStep
) : IStartupStep
{
    public string Name  => "设置初始化";
    public int    Order => 40;

    public async Task ExecuteAsync(StartupContext context, CancellationToken cancellationToken = default)
    {
        SetupSettings(context);

        EnsureSettingsInitialized(context);
        context.AccountManager = new AccountManager(context.Settings);

        var credTypeApplyResult = await context.AccountManager.InitializeCredProviderAsync(context.Settings.CredType);
        context.CredTypeApplyResult = credTypeApplyResult;

        if (!credTypeApplyResult.Succeeded)
            throw new InvalidOperationException(credTypeApplyResult.UserMessage ?? "自动登录加密方式初始化失败");

        if (credTypeApplyResult.WasFallbackApplied)
            context.Settings.CredType = credTypeApplyResult.AppliedCredType;

        context.Settings.Save();
    }

    private static void EnsureSettingsInitialized(StartupContext context)
    {
        if (context.Settings == null)
            throw new InvalidOperationException("设置初始化完成后未生成有效设置对象");
    }

    private void SetupSettings(StartupContext context)
    {
        context.Settings = LoadSettings();

        if (LogInit.LevelSwitch != null && context.Settings.EnableVerboseLog)
            LogInit.LevelSwitch.MinimumLevel = LogEventLevel.Verbose;

        if (LogInit.LevelSwitch != null)
            Log.Information("当前日志级别: {LevelSwitchMinimumLevel}", LogInit.LevelSwitch.MinimumLevel);
        else
            Log.Information("当前日志级别: 未初始化");

        try
        {
            var cmdLine = commandLineStep.GetOptions();
            context.Settings.Update
            (
                settings =>
                {
                    settings.EnableVerboseLog = false;

                    if (!string.IsNullOrEmpty(cmdLine.AccountName))
                    {
                        settings.CurrentAccountID = cmdLine.AccountName;
                        Log.Verbose("账号覆盖: '{AccountName}'", cmdLine.AccountName);
                    }

                    if (cmdLine.ClientLanguage != null)
                        settings.Language = ClientLanguage.ChineseSimplified;
                }
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法应用命令行设置覆盖");
        }
    }

    private static LauncherSettingsV3 LoadSettings() =>
        LauncherSettingsV3.Load(Paths.GetConfigPath());
}
