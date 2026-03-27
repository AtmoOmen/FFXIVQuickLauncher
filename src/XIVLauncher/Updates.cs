#nullable enable
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using Velopack;
using XIVLauncher.Common.Util;
using XIVLauncher.Settings;
using XIVLauncher.Support;
using XIVLauncher.Windows;

namespace XIVLauncher;

internal class Updates
{
    private const string UpdateUrl = "https://github.com/AtmoOmen/FFXIVQuickLauncher";
    private readonly ILauncherSettingsV3 settings;

    public Updates(ILauncherSettingsV3 settings) =>
        this.settings = settings;

    public async Task<bool> Run(bool downloadPrerelease, ChangelogWindow? changelogWindow, Action? beforeShowChangelog = null)
    {
#if XL_NOAUTOUPDATE
        return true;
#else
        _ = downloadPrerelease;

        try
        {
            if (GameHelpers.CheckIsGameOpen())
            {
                Log.Information("游戏正在运行，跳过启动器更新检查。");
                return true;
            }

            var updateOptions = new UpdateOptions
            {
                ExplicitChannel       = "win",
                AllowVersionDowngrade = true
            };

            var updateSource = new GitHubSource
            (
                UpdateUrl,
                settings.GitHubToken,
                true,
                "https://gh.atmoomen.top/",
                new XLHttpClientFileDownloader()
            );

            var updateManager = new UpdateManager(updateSource, updateOptions);
            var newRelease    = await updateManager.CheckForUpdatesAsync();

            if (newRelease == null)
                return true;

            var changelog = newRelease.TargetFullRelease.NotesMarkdown;
            await updateManager.DownloadUpdatesAsync(newRelease);

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
                        changelogWindow.ChangeLogText.Text = changelog;
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

            var updateFailHint = "请检查网络、代理链路与安全软件设置。若问题持续，请稍后重试，并将 XIVLauncherCN 加入安全软件白名单。";
            var detailMessage  = GetUpdateFailureMessage(ex);

            CustomMessageBox.Show
            (
                $"错误：{detailMessage}{Environment.NewLine}{Environment.NewLine}{updateFailHint}",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                showOfficialLauncher: true
            );

            if (settings.EnableSkipUpdate.GetValueOrDefault(false))
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
#endif
    }

    private static string GetUpdateFailureMessage(Exception exception)
    {
        if (exception is TimeoutException timeoutException)
            return timeoutException.Message;

        if (exception is HttpRequestException httpRequestException &&
            httpRequestException.StatusCode.HasValue)
        {
            return (int)httpRequestException.StatusCode switch
            {
                403 or 444 or 522 => $"更新源返回错误状态码 {(int)httpRequestException.StatusCode}。",
                _                 => $"更新请求失败，状态码：{(int)httpRequestException.StatusCode}。"
            };
        }

        if (exception is OperationCanceledException)
            return "更新请求已被取消。";

        return exception.Message;
    }
}
