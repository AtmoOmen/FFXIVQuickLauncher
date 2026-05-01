using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using XIVLauncher.Common.Addon;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Windows;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel.MainWindow.Factories;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Services;

public sealed class GameLaunchService
(
    Window window
)
{
    private readonly ConcurrentDictionary<int, AddonManager> addonManagers = [];

    public AddonManager? StartAddons(int gamePid)
    {
        var addonManager = new AddonManager();
        if (!addonManagers.TryAdd(gamePid, addonManager))
        {
            Log.Information("附加程序已随游戏进程启动: {GamePid}", gamePid);
            return null;
        }

        try
        {
            App.Settings.AddonList ??= [];

            var addons = App.Settings.AddonList
                                  .Where(entry => entry.IsEnabled && entry.Addon != null)
                                  .Select(entry => entry.Addon)
                                  .Cast<IAddon>()
                                  .ToList();

            addonManager.RunAddons(gamePid, addons);
            return addonManager;
        }
        catch
        {
            StopAddons(gamePid, addonManager);
            throw;
        }
    }

    public void StopAddons(int gamePid, AddonManager? addonManager)
    {
        if (addonManager == null)
            return;

        if (!addonManagers.TryGetValue(gamePid, out var currentAddonManager) || !ReferenceEquals(currentAddonManager, addonManager))
            return;

        if (!addonManagers.TryRemove(gamePid, out _))
            return;

        addonManager.StopAddons();
    }

    public void StartAddonsUntilGameExit(int gamePid)
    {
        var addonManager = StartAddons(gamePid);
        if (addonManager == null)
            return;

        _ = Task.Run
        (
            async () =>
            {
                try
                {
                    using var process = Process.GetProcessById(gamePid);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "等待游戏进程退出时发生错误: {GamePid}", gamePid);
                }
                finally
                {
                    StopAddons(gamePid, addonManager);
                }
            }
        );
    }

    public bool InjectGameAndAddon(int gamePid, bool noThird = false, bool noPlugins = false)
    {
        var gameExePath   = Process.GetProcessById(gamePid).MainModule?.FileName;
        var gameExeFolder = Path.GetDirectoryName(gameExePath);
        var gamePath      = new DirectoryInfo(gameExeFolder!).Parent;

        if (gamePath == null)
        {
            CustomMessageBox.Show
            (
                "无法解析游戏目录, 注入失败",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: window
            );
            return false;
        }

        EnsureDalamudCompatibility();

        var dalamudLauncher = DalamudLauncherFactory.Create(gamePath, DalamudLoadMethod.DllInject, noPlugins, noThird);
        var dalamudOk       = EnsureDalamudUpdate(dalamudLauncher, App.Settings.GamePath, true);

        Troubleshooting.LogTroubleshooting();

        if (!dalamudOk)
        {
            CustomMessageBox.Show
            (
                "Dalamud 尚未准备完成, 注入失败",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            return false;
        }

        dalamudLauncher.Inject(gamePid, noPlugins);
        return true;
    }

    public void EnsureDalamudCompatibility()
    {
        var dalamudCompatCheck = new WindowsDalamudCompatibilityCheck();

        try
        {
            dalamudCompatCheck.EnsureCompatibility();
        }
        catch (IDalamudCompatibilityCheck.NoRedistsException ex)
        {
            Log.Error(ex, "[MainWindow] 未找到 Dalamud 所需的 Redists");

            CustomMessageBox.Show
            (
                "Dalamud 需要安装 Microsoft Visual C++ 2015-2019 Redistributable, 请前往微软官网下载并安装",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                parentWindow: window
            );
        }
        catch (IDalamudCompatibilityCheck.ArchitectureNotSupportedException ex)
        {
            Log.Error(ex, "[MainWindow] 不受支持的本地环境架构");

            CustomMessageBox.Show
            (
                "Dalamud 仅支持 64 位 Windows\n若本机为 ARM 架构, 请检查是否已为 XIVLauncher 启用 64 位模拟器",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                parentWindow: window
            );
        }
    }

    private bool EnsureDalamudUpdate(DalamudLauncher dalamudLauncher, DirectoryInfo gamePath, bool appendWafStatusCodeHint)
    {
        try
        {
            App.DalamudUpdater.Run(true);
            var dalamudStatus = dalamudLauncher.HoldForUpdate(gamePath);
            return dalamudStatus == DalamudLauncher.DalamudInstallState.Ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MainWindow] 尝试更新 Dalamud 时发生错误");

            var ensurementErrorMessage = "下载 Dalamud 相关文件异常\n请检查本地网络连接, 或关闭杀毒软件\n游戏将照常启动, 但无法使用 Dalamud";

            if (appendWafStatusCodeHint
                && ex is HttpRequestException httpRequestException
                && httpRequestException.StatusCode.HasValue
                && (int)httpRequestException.StatusCode is 403 or 444 or 522)
                ensurementErrorMessage = $"服务器错误: {httpRequestException.StatusCode}\n{ensurementErrorMessage}";
            else
                ensurementErrorMessage = $"错误: {ex.Message}\n{ensurementErrorMessage}";

            CustomMessageBox.Builder
                            .NewFrom(ensurementErrorMessage)
                            .WithImage(MessageBoxImage.Warning)
                            .WithButtons(MessageBoxButton.OK)
                            .WithShowHelpLinks()
                            .WithParentWindow(window)
                            .Show();
            return false;
        }
    }
}
