#nullable enable
using System;
using System.Diagnostics;
using System.Net;
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
(
    ILauncherSettingsV3 settings
)
{
    private const string UPDATE_URL = "https://github.com/AtmoOmen/FFXIVQuickLauncher";

    public async Task Run(bool downloadPrerelease, ChangelogWindow? changelogWindow)
    {
#if XL_NOAUTOUPDATE
        OnUpdateCheckFinished?.Invoke(true);
        return;
#endif
        
        try
        {
            // 游戏进程
            if (GameHelpers.CheckIsGameOpen())
            {
                Log.Information("游戏正在运行, 跳过启动器更新检查");
                OnUpdateCheckFinished?.Invoke(true);
                return;
            }

            var updateOptions = new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = true };
            var updateSource  = new GitHubSource(UPDATE_URL, settings.GitHubToken, true, "https://gh.atmoomen.top/", new XLHttpClientFileDownloader());
            var mgr           = new UpdateManager(updateSource, updateOptions);

            var newRelease = await mgr.CheckForUpdatesAsync();

            if (newRelease != null)
            {
                var changelog = newRelease.TargetFullRelease.NotesMarkdown;
                await mgr.DownloadUpdatesAsync(newRelease);

                if (changelogWindow == null)
                {
                    Log.Error("changelogWindow was null");
                    mgr.ApplyUpdatesAndRestart(newRelease);
                    return;
                }

                try
                {
                    changelogWindow.Dispatcher.Invoke
                    (() =>
                        {
                            changelogWindow.UpdateVersion(newRelease.TargetFullRelease.Version.ToString());
                            changelogWindow.ChangeLogText.Text = changelog;
                            changelogWindow.Show();
                            changelogWindow.Closed += (_, _) => { mgr.ApplyUpdatesAndRestart(newRelease); };
                        }
                    );

                    OnUpdateCheckFinished?.Invoke(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "无法显示更新日志窗口");
                }
            }
            else
                OnUpdateCheckFinished?.Invoke(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新失败");

            var updateFailLoc = "XIVLauncherCN 检查更新失败, 请检查你的网络环境并将 XIVLauncherCN 加入杀毒软件白名单中";

            if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode.HasValue && (int)httpRequestException.StatusCode is 403 or 444 or 522)
            {
                CustomMessageBox.Show
                (
                    $"错误: GitHub 服务器返回错误代码 {httpRequestException.StatusCode}.\n" + Environment.NewLine + updateFailLoc,
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    showOfficialLauncher: true
                );
            }
            else
            {
                CustomMessageBox.Show
                (
                    $"错误: {ex.Message}" + Environment.NewLine + updateFailLoc,
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    showOfficialLauncher: true
                );
            }

            if (settings.EnableSkipUpdate.GetValueOrDefault(false))
            {
                var result = CustomMessageBox.Show
                (
                    "无法检查更新, 根据你的设置, 是否继续使用当前版本?\n" + "请注意: 这说明你当前可能无法连接 Github, 即使进入 XIVLauncher 也无法完成 Dalamud 的更新检查与下载",
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    showDiscordLink: false,
                    showHelpLinks: false
                );

                if (result == MessageBoxResult.Yes) OnUpdateCheckFinished?.Invoke(true);
                else Environment.Exit(1);
            }
            else Environment.Exit(1);
        }

        ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault;
    }

    public event Action<bool>? OnUpdateCheckFinished;
}
