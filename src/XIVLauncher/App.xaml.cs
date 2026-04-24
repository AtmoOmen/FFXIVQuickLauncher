using System;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using XIVLauncher.Accounts;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Settings;
using XIVLauncher.Startup;
using XIVLauncher.Startup.Elevation;
using XIVLauncher.Windows;

namespace XIVLauncher;

public partial class App
{
    #region 静态属性

    public static StartupContext StartupContext { get; private set; } = null!;

    public static LauncherSettingsV3 Settings
    {
        get
        {
            var context = GetStartupContext();
            return context.Settings ?? throw new InvalidOperationException("设置尚未初始化");
        }
    }

    public static AccountManager AccountManager
    {
        get
        {
            var context = GetStartupContext();
            return context.AccountManager ?? throw new InvalidOperationException("账号管理器尚未初始化");
        }
    }

    public static DalamudUpdater DalamudUpdater
    {
        get
        {
            var context = GetStartupContext();
            return context.DalamudUpdater ?? throw new InvalidOperationException("Dalamud 更新器尚未初始化");
        }
    }

    #endregion

    #region 字段

    private StartupOrchestrator? orchestrator;
    private MainWindow?          mainWindow;
    private bool                 isUseFullExceptionHandler;

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
        try
        {
            if (StartupElevationService.TryRestartElevatedAndExit())
                return;

            orchestrator   = new(Dispatcher);
            StartupContext = orchestrator.GetContext();

            try
            {
                await orchestrator.RunAsync();

                if (StartupContext.IsRestartingForUpdate)
                    return;

                OnStartupCompleted();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动流程失败");
                MessageBox.Show($"启动失败: {ex.Message}", "XIVLauncher 错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(-1);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "早期启动流程失败");
            Environment.Exit(-1);
        }
    }

    private void OnStartupCompleted()
    {
        isUseFullExceptionHandler = true;

        mainWindow = new MainWindow();
        mainWindow.Initialize();

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

    private static StartupContext GetStartupContext() =>
        StartupContext ?? throw new InvalidOperationException("启动上下文尚未初始化");
}
