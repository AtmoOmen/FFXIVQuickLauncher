using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CommandLine;
using Serilog;
using Serilog.Events;
using Velopack;
using XIVLauncher.Account;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
#if !XL_NOUPDATE
using XIVLauncher.Common.Http;
#endif
using XIVLauncher.Common.Support;
using XIVLauncher.Dalamud;
using XIVLauncher.Settings;
using XIVLauncher.Support;
using XIVLauncher.Windows;
using DateTimeOffset = System.DateTimeOffset;

namespace XIVLauncher.Startup;

public class StartupOrchestrator
(
    Dispatcher dispatcher
)
{
    private readonly StartupContext context = new()
    {
        Dispatcher = dispatcher
    };

    private CommandLineOptions commandLineOptions = new();
    private LoadingDialog?     updateWindow;
    private StartupDalamudProgressSink? dalamudProgressSink;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        InitializeRenderMode();
        InitializeLogging();
        ParseCommandLine();

        Log.Information("开始执行启动流程");

        cancellationToken.ThrowIfCancellationRequested();
        await InitializeSettingsAsync();

        cancellationToken.ThrowIfCancellationRequested();
        InitializeVelopack();

        cancellationToken.ThrowIfCancellationRequested();
        await CheckUpdatesAsync();

        if (context.IsRestartingForUpdate)
        {
            Log.Information("检测到启动器即将重启更新，提前结束剩余启动步骤");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        InitializeDalamud();

        Log.Information("启动流程完成");
    }

    public StartupContext GetContext() => context;

    private static void InitializeRenderMode()
    {
        try
        {
            if (!EnvironmentSettings.IsHardwareRendered)
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
        catch
        {
            // ignored
        }
    }

    private static void InitializeLogging()
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
    }

    private static void OnSerilogLogLine
    (
        object?                                                                            sender,
        (string Line, LogEventLevel Level, DateTimeOffset TimeStamp, Exception? Exception) e
    )
    {
        if (e.Exception == null)
            return;

        Troubleshooting.LogException(e.Exception, e.Line);
    }

    private void ParseCommandLine()
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

            commandLineOptions = result.Value ?? new();
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法解析命令行参数, 请反馈此问题\n\n" + ex.Message, "XIVLauncherCN (Soil)", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    private async Task InitializeSettingsAsync()
    {
        context.Settings = LauncherSettingsV3.Load(Paths.GetConfigPath());

        if (LogInit.LevelSwitch != null && context.Settings.EnableVerboseLog)
            LogInit.LevelSwitch.MinimumLevel = LogEventLevel.Verbose;

        if (LogInit.LevelSwitch != null)
            Log.Information("当前日志级别: {LevelSwitchMinimumLevel}", LogInit.LevelSwitch.MinimumLevel);
        else
            Log.Information("当前日志级别: 未初始化");

        try
        {
            context.Settings.Update
            (settings =>
                {
                    settings.EnableVerboseLog = false;

                    if (!string.IsNullOrEmpty(commandLineOptions.AccountName))
                    {
                        settings.CurrentAccountID = commandLineOptions.AccountName;
                        Log.Verbose("账号覆盖: '{AccountName}'", commandLineOptions.AccountName);
                    }
                }
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法应用命令行设置覆盖");
        }

        context.AccountManager = new AccountManager(context.Settings);

        var credTypeApplyResult = await context.AccountManager.InitializeCredProviderAsync(context.Settings.CredType);
        context.CredTypeApplyResult = credTypeApplyResult;

        if (!credTypeApplyResult.Succeeded)
            throw new InvalidOperationException(credTypeApplyResult.UserMessage ?? "自动登录加密方式初始化失败");

        if (credTypeApplyResult.WasFallbackApplied)
            context.Settings.CredType = credTypeApplyResult.AppliedCredType;

        context.Settings.Save();
    }

    private static void InitializeVelopack() =>
        VelopackApp.Build().Run();

    private async Task CheckUpdatesAsync()
    {
#if !XL_NOAUTOUPDATE
        if (EnvironmentSettings.IsDisableUpdates)
            return;

        try
        {
            Log.Information("开始检查启动器更新");

            updateWindow = new();
            updateWindow.Show();

            var              updateMgr       = new Updates(context.Settings);
            ChangelogWindow? changelogWindow = null;

            try
            {
                changelogWindow = new ChangelogWindow();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "无法创建更新日志窗口");
            }

            var shouldContinueStartup = await updateMgr.Run
                                        (
                                            EnvironmentSettings.IsPreRelease,
                                            updateWindow,
                                            changelogWindow,
                                            CloseUpdateWindow
                                        ).ConfigureAwait(false);

            CloseUpdateWindow();

            if (!shouldContinueStartup)
                context.IsRestartingForUpdate = true;
        }
        catch (Exception ex)
        {
            HandleUpdateError(ex);
        }
#endif
    }

#if !XL_NOAUTOUPDATE
    private static void HandleUpdateError(Exception ex)
    {
        Log.Error(ex, "执行更新检查失败");

        if (ex.FindHttpRequestException() is { StatusCode: not null } httpRequestException && (int)httpRequestException.StatusCode is 403 or 444 or 522)
        {
            MessageBox.Show
            (
                $"错误：服务端返回错误代码 {httpRequestException.StatusCode}\n\n{httpRequestException.Message}\n\n{ex}",
                "XIVLauncher 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
        else
        {
            MessageBox.Show
            (
                $"错误：{ex.Message}{Environment.NewLine}XIVLauncher 无法检查更新，请检查网络连接后重试。{Environment.NewLine}{Environment.NewLine}{ex}",
                "XIVLauncher 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }

        Environment.Exit(0);
    }
#endif

    private void CloseUpdateWindow()
    {
        void CloseCore()
        {
            if (updateWindow == null)
                return;

            updateWindow.Close();
            updateWindow = null;
        }

        if (context.Dispatcher.CheckAccess())
        {
            CloseCore();
            return;
        }

        context.Dispatcher.Invoke(CloseCore);
    }

    private void InitializeDalamud()
    {
        try
        {
            var dalamudWindowThread = new Thread(StartDalamudOverlayThread);
            dalamudWindowThread.SetApartmentState(ApartmentState.STA);
            dalamudWindowThread.IsBackground = true;
            dalamudWindowThread.Start();

            while (dalamudProgressSink == null)
                Thread.Yield();

            context.Dalamud = new DalamudService
            (
                new DalamudHostPaths
                (
                    new(Path.Combine(Paths.RoamingPath, "addon")),
                    new(Path.Combine(Paths.RoamingPath, "runtime")),
                    new(Path.Combine(Paths.RoamingPath, "dalamudAssets")),
                    new(Path.Combine(Paths.RoamingPath, "dalamudConfig")),
                    new(Path.Combine(Paths.RoamingPath, "logs")),
                    new(Environment.CurrentDirectory)
                ),
                context.Settings.GitHubToken,
                dalamudProgressSink,
                new AppDalamudGameVersionProvider(),
                new AppDalamudTroubleshootingProvider()
            );

            context.Dalamud.RunUpdater();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法启动 Dalamud 更新器");
            throw;
        }
    }

    private void StartDalamudOverlayThread()
    {
        var overlay = new LoadingDialog("正在更新 Dalamud 框架...", true);
        overlay.Hide();
        dalamudProgressSink = new StartupDalamudProgressSink(overlay);

        Dispatcher.Run();
    }
}
