using System.Windows;
using Serilog;
using Velopack;
using Velopack.Sources;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;
using XIVLauncher.Settings;
using XIVLauncher.Support;
using XIVLauncher.Windows;

namespace XIVLauncher.Update;

internal class UpdateOrchestrator
(
    LauncherSettingsV3 settings
)
{
    public async Task<bool> Run
    (
        bool             downloadPrerelease,
        LoadingDialog?   loadingDialog,
        ChangelogWindow? changelogWindow,
        Action?          beforeShowChangelog = null
    )
    {
        _ = downloadPrerelease;

        try
        {
            var updateOptions = new UpdateOptions
            {
                ExplicitChannel       = "win",
                AllowVersionDowngrade = false
            };

            var updateSource = new SimpleWebSource
            (
                Links.LAUNCHER_DISTRIBUTE_BASE_URL,
                new XLHttpClientFileDownloader()
            );

            var updateManager = new UpdateManager(updateSource, updateOptions);
            loadingDialog?.SetMessage("正在检查启动器更新...");
            var newRelease = await updateManager.CheckForUpdatesAsync();

            if (newRelease == null)
                return true;

            var changelog = newRelease.TargetFullRelease.NotesMarkdown;
            loadingDialog?.SetMessage("正在下载启动器更新...");
            loadingDialog?.ReportProgress(0);

            await updateManager.DownloadUpdatesAsync
            (
                newRelease,
                progress =>
                {
                    loadingDialog?.SetMessage("正在下载启动器更新...");
                    loadingDialog?.ReportProgress(progress);
                }
            );

            loadingDialog?.SetMessage("正在安装启动器更新...");
            loadingDialog?.ReportProgress(100);

            if (changelogWindow == null)
            {
                Log.Error("更新日志窗口为空，直接进入更新安装流程。");
                updateManager.ApplyUpdatesAndRestart(newRelease);
                return false;
            }

            try
            {
                await changelogWindow.Dispatcher.InvokeAsync
                (() =>
                    {
                        beforeShowChangelog?.Invoke();
                        changelogWindow.UpdateVersion(newRelease.TargetFullRelease.Version.ToString());
                        changelogWindow.ChangeLogText.Markdown = changelog;
                        changelogWindow.ShowDialog();
                    }
                );

                updateManager.ApplyUpdatesAndRestart(newRelease);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "无法显示更新日志窗口，直接进入更新安装流程。");
                updateManager.ApplyUpdatesAndRestart(newRelease);
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动器更新失败");

            const string UPDATE_FAIL_HINT = "请检查网络、代理链路与安全软件设置。若问题持续，请稍后重试，并将 XIVLauncherCN 加入安全软件白名单。";

            var detailMessage = GetUpdateFailureMessage(ex);

            CustomMessageBox.Show
            (
                $"错误：{detailMessage}{Environment.NewLine}{Environment.NewLine}{UPDATE_FAIL_HINT}",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                showOfficialLauncher: true
            );

            if (settings.EnableSkipUpdate)
            {
                var result = CustomMessageBox.Show
                (
                    "无法完成更新检查。根据你的设置，是否继续使用当前版本？\n请注意：这通常意味着当前无法稳定连接更新源，即使进入 XIVLauncher，也可能无法完成 Dalamud 的更新检查与下载。",
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    showDiscordLink: false,
                    showHelpLinks: false
                );

                return result == MessageBoxResult.Yes;
            }

            Environment.Exit(1);
            return false;
        }
    }

    internal static string GetUpdateFailureMessage(Exception exception) =>
        exception switch
        {
            TimeoutException timeoutException => timeoutException.Message,
            Exception when exception.FindHttpRequestException() is { StatusCode: not null } httpRequestException => (int)httpRequestException.StatusCode switch
            {
                403 or 444 or 522 => $"更新源返回错误状态码 {(int)httpRequestException.StatusCode}{Environment.NewLine}{httpRequestException.Message}",
                _                 => $"更新请求失败, 状态码 {(int)httpRequestException.StatusCode}{Environment.NewLine}{httpRequestException.Message}"
            },
            OperationCanceledException => "更新请求已被取消。",
            _                          => exception.Message
        };
}
