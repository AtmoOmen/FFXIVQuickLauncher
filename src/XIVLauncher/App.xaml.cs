using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using XIVLauncher.Accounts;
using XIVLauncher.Common;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Settings;
using XIVLauncher.Startup;
using XIVLauncher.Windows;

namespace XIVLauncher;

public partial class App
{
    #region 静态属性

    public static StartupContext StartupContext { get; private set; } = null!;

    public static ILauncherSettingsV3 Settings       => StartupContext.Settings;
    public static AccountManager      AccountManager => StartupContext.AccountManager;
    public static DalamudUpdater      DalamudUpdater => StartupContext.DalamudUpdater;
    public static bool                InjectMode     => StartupContext.InjectMode;
    public static bool GlobalIsDisableAutologin
    {
        get => StartupContext.IsDisableAutologin;
        set => StartupContext.IsDisableAutologin = value;
    }

    #endregion

    #region 字段

    private StartupOrchestrator? orchestrator;
    private MainWindow? mainWindow;
    private bool isUseFullExceptionHandler;

    #endregion

    #region 入口

    public App()
    {
#if !DEBUG
        try
        {
            AppDomain.CurrentDomain.UnhandledException += OnEarlyInitException;
            TaskScheduler.UnobservedTaskException      += OnUnobservedTaskException;
        }
        catch
        {
            // ignored
        }
#endif
    }

    private async void App_OnStartup(object sender, StartupEventArgs e)
    {
        orchestrator = new StartupOrchestrator(this, Dispatcher);

        try
        {
            await orchestrator.RunAsync();

            StartupContext = orchestrator.GetContext();

            OnStartupCompleted();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动流程失败");
            MessageBox.Show($"启动失败: {ex.Message}", "XIVLauncher 错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(-1);
        }
    }

    private void OnStartupCompleted()
    {
        isUseFullExceptionHandler = true;

        mainWindow = new MainWindow();
        mainWindow.Initialize();

        if (StartupContext.InjectMode)
            mainWindow.Model.InjectModeSwitchCommand.Execute(null);
    }

    #endregion

    #region 事件处理

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (e.Observed) return;

        OnEarlyInitException(sender, new UnhandledExceptionEventArgs(e.Exception, true));
    }

    private void OnEarlyInitException(object? sender, UnhandledExceptionEventArgs e)
    {
        Dispatcher.Invoke
        (() =>
            {
                Log.Error((Exception)e.ExceptionObject, "未处理的异常");

                if (isUseFullExceptionHandler)
                {
                    CustomMessageBox.Builder
                                    .NewFrom((Exception)e.ExceptionObject, "未处理", CustomMessageBox.ExitOnCloseModes.ExitOnClose)
                                    .WithAppendText("\n\n初始化早期阶段发生错误, 请反馈此问题\n\n" + e.ExceptionObject)
                                    .Show();
                }
                else
                {
                    MessageBox.Show
                    (
                        "初始化早期阶段发生错误, 请反馈此问题\n\n" + e.ExceptionObject,
                        "XIVLauncher 错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }

                Environment.Exit(-1);
            }
        );
    }

    #endregion

    #region 嵌套类型

    public class CommandLineOptions
    {
        [CommandLine.Option("dalamud-runner-override", Required = false, HelpText = "用于覆盖 Dalamud 运行器的文件夹路径")]
        public string RunnerOverride { get; set; } = null!;

        [CommandLine.Option("roamingPath", Required = false, HelpText = "用于覆盖 XIVLauncher 漫游路径的文件夹路径")]
        public string RoamingPath { get; set; } = null!;

        [CommandLine.Option("noautologin", Required = false, HelpText = "禁用自动登录")]
        public bool NoAutoLogin { get; set; }

        [CommandLine.Option("gen-localizable", Required = false, HelpText = "生成本地化文件")]
        public bool DoGenerateLocalizables { get; set; }

        [CommandLine.Option("gen-integrity", Required = false, HelpText = "生成完整性校验文件, 请提供游戏路径")]
        public string DoGenerateIntegrity { get; set; } = null!;

        [CommandLine.Option("account", Required = false, HelpText = "要使用的账号名称")]
        public string AccountName { get; set; } = null!;

        [CommandLine.Option("clientlang", Required = false, HelpText = "要使用的客户端语言")]
        public ClientLanguage? ClientLanguage { get; set; }

        [CommandLine.Option("squirrel-updated", Hidden = true)]
        public string SquirrelUpdated { get; set; } = null!;

        [CommandLine.Option("squirrel-install", Hidden = true)]
        public string SquirrelInstall { get; set; } = null!;

        [CommandLine.Option("squirrel-obsolete", Hidden = true)]
        public string SquirrelObsolete { get; set; } = null!;

        [CommandLine.Option("squirrel-uninstall", Hidden = true)]
        public string SquirrelUninstall { get; set; } = null!;

        [CommandLine.Option("squirrel-firstrun", Hidden = true)]
        public bool SquirrelFirstRun { get; set; }

        [CommandLine.Option("inject", Hidden = true)]
        public bool InjectMode { get; set; }
    }

    #endregion
}
