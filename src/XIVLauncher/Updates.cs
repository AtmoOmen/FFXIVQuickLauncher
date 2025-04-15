#nullable enable
using CheapLoc;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using XIVLauncher.Support;
using XIVLauncher.Windows;

namespace XIVLauncher;

internal class Updates
{
    public event Action<bool>? OnUpdateCheckFinished;
    private const string       UpdateUrl = "https://github.com/AtmoOmen/FFXIVQuickLauncher";

    public static Lease? UpdateLease { get; private set; }

    [Flags]
    public enum LeaseFeatureFlags
    {
        None                       = 0,
        GlobalDisableDalamud       = 1,
        ForceProxyDalamudAndAssets = 1 << 1
    }

#pragma warning disable CS8618
    public class Lease
    {
        public bool              Success       { get; set; }
        public string?           Message       { get; set; }
        public string?           CutOffBootver { get; set; }
        public string            FrontierUrl   { get; set; }
        public LeaseFeatureFlags Flags         { get; set; }

        public string ReleasesList { get; set; }

        public DateTime? ValidUntil { get; set; }
    }
#pragma warning restore CS8618

    public static bool HaveFeatureFlag(LeaseFeatureFlags flag)
    {
        return UpdateLease != null && UpdateLease.Flags.HasFlag(flag);
    }

    public async Task Run(bool downloadPrerelease, ChangelogWindow? changelogWindow)
    {
#if RELEASENOUPDATE
            OnUpdateCheckFinished?.Invoke(true);
            return;
#endif
        // GitHub requires TLS 1.2, we need to hardcode this for Windows 7
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

        try
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");
                if (!string.IsNullOrWhiteSpace(App.Settings.GitHubToken))
                    httpClient.DefaultRequestHeaders.Authorization = new("Bearer", App.Settings.GitHubToken);
                var response = await httpClient.GetAsync("https://api.github.com/rate_limit");
                response.EnsureSuccessStatusCode();

                var     json      = await response.Content.ReadAsStringAsync();
                dynamic rateLimit = JObject.Parse(json);
                int     remaining = rateLimit.resources.core.remaining;

                if (remaining == 0)
                {
                    int resetTimestamp = rateLimit.resources.core.reset;
                    var resetTime      = DateTimeOffset.FromUnixTimeSeconds(resetTimestamp).LocalDateTime;
                    CustomMessageBox.Show($"当前 IP 的 GitHub API 调用额度已用尽, 下次刷新时间: {resetTime:HH:mm:ss}\n" +
                                          $"请在设置中填写 GitHub Access Token 或耐心等待 / 更换你的网络环境\n" +
                                          $"如果你不清楚如何更换网络环境, 请勿询问并立刻卸载本软件, 多谢配合",
                                          "XIVLauncherCN",
                                          MessageBoxButton.OK,
                                          MessageBoxImage.Error);
                    Environment.Exit(1);
                }
            }
            catch (Exception ex)
            {
                if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode is HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(App.Settings.GitHubToken))
                {
                    var builder = new CustomMessageBox.Builder()
                        .WithCaption("XIVLauncherCN")
                        .WithText($"当前配置的 GitHub Token 已失效, 请重新配置或删除 Token\n原 Token: {App.Settings.GitHubToken}")
                        .WithButtons(MessageBoxButton.OK)
                        .WithImage(MessageBoxImage.Error)
                        .WithShowHelpLinks()
                        .WithShowDiscordLink()
                        .WithInputTextBox(App.Settings.GitHubToken);

                    if (builder.Show() == MessageBoxResult.OK)
                    {
                        App.Settings.GitHubToken = builder.InputTextBoxText;
                    }
                }
                else
                {
                    Log.Warning(ex, "GitHub 速率限制检查失败, 继续尝试更新");
                }
            }

            // 游戏进程
            if (System.Diagnostics.Process.GetProcessesByName("ffxiv_dx11").Length > 0)
            {
                Log.Information("游戏正在运行, 跳过启动器更新检查");
                this.OnUpdateCheckFinished?.Invoke(true);
                return;
            }

            var updateOptions = new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = true };
            var updateSource  = new GitHubSource(UpdateUrl, App.Settings.GitHubToken, true);
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
                    changelogWindow.Dispatcher.Invoke(() =>
                    {
                        changelogWindow.UpdateVersion(newRelease.TargetFullRelease.Version.ToString());
                        changelogWindow.ChangeLogText.Text = changelog;
                        changelogWindow.Show();
                        changelogWindow.Closed += (_, _) => { mgr.ApplyUpdatesAndRestart(newRelease); };
                    });

                    this.OnUpdateCheckFinished?.Invoke(false);
                }
                catch (Exception ex) { Log.Error(ex, "无法显示更新日志窗口"); }
            }
            else
                this.OnUpdateCheckFinished?.Invoke(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新失败");

            var updateFailLoc = Loc.Localize("updatefailureerror",
                                             "XIVLauncherCN 检查更新失败, 请检查你的网络环境并将 XIVLauncherCN 加入杀毒软件白名单中");

            if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode.HasValue &&
                (int)httpRequestException.StatusCode is 403 or 444 or 522)
            {
                CustomMessageBox.Show($"错误: GitHub 服务器返回错误代码 {httpRequestException.StatusCode}.\n" +
                                      Environment.NewLine                                          + updateFailLoc,
                                      "XIVLauncherCN",
                                      MessageBoxButton.OK,
                                      MessageBoxImage.Error, showOfficialLauncher: true);
            }
            else
            {
                CustomMessageBox.Show($"错误: {ex.Message}" + Environment.NewLine + updateFailLoc,
                                      "XIVLauncherCN",
                                      MessageBoxButton.OK,
                                      MessageBoxImage.Error, showOfficialLauncher: true);
            }

            if (App.Settings.EnableSkipUpdate.GetValueOrDefault(false))
            {
                var result = CustomMessageBox.Show("无法检查更新, 根据你的设置, 是否继续使用当前版本?\n" +
                                                   "请注意: 这说明你当前可能无法连接 Github, 即使进入 XIVLauncher 也无法完成 Dalamud 的更新检查与下载",
                                                   "XIVLauncherCN",
                                                   MessageBoxButton.YesNo,
                                                   MessageBoxImage.Question,
                                                   showDiscordLink: false,
                                                   showHelpLinks: false);

                if (result == MessageBoxResult.Yes) this.OnUpdateCheckFinished?.Invoke(true);
                else Environment.Exit(1);
            }
            else Environment.Exit(1);
        }

        ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault;
    }
}
