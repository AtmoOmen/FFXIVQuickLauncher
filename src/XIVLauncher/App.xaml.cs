using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Util;
using XIVLauncher.Dalamud;
using XIVLauncher.Settings;
using XIVLauncher.Startup;
using XIVLauncher.Windows;
using XIVLauncher.Windows.Main;

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

    public static DalamudService Dalamud
    {
        get
        {
            var context = GetStartupContext();
            return context.Dalamud ?? throw new InvalidOperationException("Dalamud 服务尚未初始化");
        }
    }

    #endregion

    #region 字段

    private StartupOrchestrator? orchestrator;
    private MainWindow?          mainWindow;

    private static readonly SearchValues<char> CommandArgsSearchValues = SearchValues.Create(" \t\n\v\"");

    private bool isUseFullExceptionHandler;

    #endregion

    #region 入口

    public App()
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException      += OnUnobservedTaskException;
        }
        catch
        {
            // ignored
        }
    }

    private async void App_OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            if (TryRestartElevatedAndExit())
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

        OnUnhandledException(sender, new UnhandledExceptionEventArgs(e.Exception, true));
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Dispatcher.Invoke
        (() =>
            {
                var exception = (Exception)e.ExceptionObject;

                Log.Error(exception, "未处理的异常");

                if (isUseFullExceptionHandler)
                {
                    CustomMessageBox.Builder
                                    .NewFrom(exception, "未处理", CustomMessageBox.ExitOnCloseModes.ExitOnClose)
                                    .WithAppendText($"\n\n发生未处理的异常\n\n{exception.StackTrace}")
                                    .Show();
                }
                else
                {
                    MessageBox.Show
                    (
                        $"发生未处理的异常\n\n{exception.StackTrace}",
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

    private static bool TryRestartElevatedAndExit()
    {
        if (PlatformHelpers.IsElevated())
            return false;

        var executablePath = Paths.ResolveExecutablePath();
        var arguments      = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(EncodeParameterArgument));
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            Verb            = "runas",
            Arguments       = arguments
        };

        try
        {
            Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (PlatformHelpers.IsWindowsErrorCancelled(ex))
        {
            return false;
        }

        Environment.Exit(0);
        return true;
    }

    private static string EncodeParameterArgument(string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(CommandArgsSearchValues) == -1)
            return argument;

        var quoted = new StringBuilder(argument.Length * 2);
        quoted.Append('\"');

        var numberBackslashes = 0;

        foreach (var chr in argument)
        {
            switch (chr)
            {
                case '\\':
                    numberBackslashes++;
                    continue;

                case '\"':
                    quoted.Append('\\', numberBackslashes * 2 + 1);
                    break;

                default:
                    quoted.Append('\\', numberBackslashes);
                    break;
            }

            quoted.Append(chr);

            numberBackslashes = 0;
        }

        quoted.Append('\\', numberBackslashes * 2);
        quoted.Append('\"');

        return quoted.ToString();
    }
}
