using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using CheapLoc;
using Newtonsoft.Json;
using Serilog;
using Velopack;
using Velopack.Sources;
using XIVLauncher.Common.Util;
using XIVLauncher.Windows;

#nullable enable

namespace XIVLauncher
{
    internal class Updates
    {
        public event Action<bool>? OnUpdateCheckFinished;
        private const string       UPDATE_URL = "https://github.com/AtmoOmen/FFXIVQuickLauncher";

        public static Lease? UpdateLease { get; private set; }

        [Flags]
        public enum LeaseFeatureFlags
        {
            None                       = 0,
            GlobalDisableDalamud       = 1,
            ForceProxyDalamudAndAssets = 1 << 1,
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
                    var response = await httpClient.GetAsync("https://api.github.com/rate_limit");
                    response.EnsureSuccessStatusCode();

                    var     json      = await response.Content.ReadAsStringAsync();
                    dynamic rateLimit = Newtonsoft.Json.Linq.JObject.Parse(json);
                    int     remaining = rateLimit.resources.core.remaining;

                    if (remaining == 0)
                    {
                        int resetTimestamp = rateLimit.resources.core.reset;
                        var resetTime      = DateTimeOffset.FromUnixTimeSeconds(resetTimestamp).LocalDateTime;
                        CustomMessageBox.Show($"当前 IP 的 GitHub API 速率额度已用尽, 下次刷新时间: {resetTime:HH:mm:ss}\n" + 
                                              $"请耐心等待或更换你的网络环境",
                                              "XIVLauncherCN",
                                              MessageBoxButton.OK,
                                              MessageBoxImage.Error);
                        Environment.Exit(1);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "GitHub 速率限制检查失败, 继续尝试更新");
                }

                var updateOptions = new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = true };
                var updateSource  = new GithubSource(UPDATE_URL, null, true);
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

                        OnUpdateCheckFinished?.Invoke(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "无法显示更新日志窗口");
                    }
                }
                else { this.OnUpdateCheckFinished?.Invoke(true); }
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
                                          Environment.NewLine + updateFailLoc,
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
                    var result = CustomMessageBox.Show("无法检查更新，是否继续使用当前版本?",
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
}

#nullable restore
