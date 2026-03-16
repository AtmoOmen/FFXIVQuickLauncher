using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Windows;

namespace XIVLauncher.Startup.Steps;

public class UpdateCheckStep
(
    CommandLineStep commandLineStep
) : IStartupStep
{
    public string Name  => "更新检查";
    public int    Order => 70;

    public  FileInfo?            DalamudRunnerOverride => commandLineStep.DalamudRunnerOverride;
    private UpdateLoadingDialog? updateWindow;

    public async Task ExecuteAsync(StartupContext context, CancellationToken cancellationToken = default)
    {
#if !XL_NOAUTOUPDATE
        if (EnvironmentSettings.IsDisableUpdates)
        {
            context.IsUpdateFinished = true;
            return;
        }

        try
        {
            Log.Information("开始检查更新...");

            updateWindow = new();
            updateWindow.Show();

            if (context.Settings == null)
                throw new InvalidOperationException("更新检查阶段无法获取设置对象");

            var updateMgr = new Updates(context.Settings);
            updateMgr.OnUpdateCheckFinished += finishUp => OnUpdateCheckFinished(context, finishUp);

            ChangelogWindow? changelogWindow = null;

            try
            {
                changelogWindow = new ChangelogWindow();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "无法加载更新日志窗口");
            }

            await updateMgr.Run(EnvironmentSettings.IsPreRelease, changelogWindow).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HandleUpdateError(ex);
        }
#else
        context.IsUpdateFinished = true;
#endif
    }

#if !XL_NOAUTOUPDATE
    private static void HandleUpdateError(Exception ex)
    {
        Log.Error(ex, "无法执行更新检查");

        if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode.HasValue && (int)httpRequestException.StatusCode is 403 or 444 or 522)
        {
            MessageBox.Show
            (
                "错误: " + $"服务器返回了错误代码 {httpRequestException.StatusCode}" + ex,
                "XIVLauncher 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
        else
        {
            MessageBox.Show
            (
                "错误: " + ex.Message + Environment.NewLine + "XIVLauncher 无法检查更新, 请检查网络连接或稍后重试\n\n" + ex,
                "XIVLauncher 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }

        Environment.Exit(0);
    }
#endif

    private void OnUpdateCheckFinished(StartupContext context, bool finishUp)
    {
        context.Dispatcher.Invoke
        (() =>
            {
                updateWindow?.Hide();

                if (!finishUp)
                {
                    UpdateCheckFinished?.Invoke(this, false);
                    return;
                }

                context.IsUpdateFinished = true;
                UpdateCheckFinished?.Invoke(this, true);
            }
        );
    }

    public event EventHandler<bool>? UpdateCheckFinished;
}
