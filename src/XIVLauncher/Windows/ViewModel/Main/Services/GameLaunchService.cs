using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Serilog;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Http;
using XIVLauncher.CompanionApp;
using XIVLauncher.Dalamud;
using XIVLauncher.Support;

namespace XIVLauncher.Windows.ViewModel.Main.Services;

public sealed class GameLaunchService
(
    Window window
)
{
    private readonly ConcurrentDictionary<int, CompanionAppManager> companionAppManagers = [];

    public CompanionAppManager? StartCompanionApps(int gamePid)
    {
        var companionAppManager = new CompanionAppManager();

        if (!companionAppManagers.TryAdd(gamePid, companionAppManager))
        {
            Log.Information("伴随程序已随游戏进程启动: {GamePid}", gamePid);
            return null;
        }

        try
        {
            App.Settings.CompanionAppList ??= [];

            var companionApps = App.Settings.CompanionAppList
                                   .Where(entry => entry is { IsEnabled: true, CompanionApp: not null })
                                   .Select(entry => entry.CompanionApp)
                                   .ToList();

            companionAppManager.Start(companionApps);
            return companionAppManager;
        }
        catch
        {
            StopCompanionApps(gamePid, companionAppManager);
            throw;
        }
    }

    public void StopCompanionApps(int gamePid, CompanionAppManager? companionAppManager)
    {
        if (companionAppManager == null)
            return;

        if (!companionAppManagers.TryGetValue(gamePid, out var currentCompanionAppManager) || !ReferenceEquals(currentCompanionAppManager, companionAppManager))
            return;

        if (!companionAppManagers.TryRemove(gamePid, out _))
            return;

        companionAppManager.Stop();
    }

    public void StartCompanionAppsUntilGameExit(int gamePid)
    {
        var companionAppManager = StartCompanionApps(gamePid);
        if (companionAppManager == null)
            return;

        _ = Task.Run
        (async () =>
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
                    StopCompanionApps(gamePid, companionAppManager);
                }
            }
        );
    }

    public bool InjectGame(int gamePid, bool noThird = false, bool noPlugins = false)
    {
        using var gameProcess = Process.GetProcessById(gamePid);
        if (gameProcess.HasExited)
        {
            CustomMessageBox.Show
            (
                "游戏进程已经退出, 注入失败",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: window
            );
            return false;
        }

        var gameExePath = gameProcess.MainModule?.FileName;
        var gameExeDir  = gameExePath == null ? null : Path.GetDirectoryName(gameExePath);
        var gamePath    = gameExeDir == null ? null : new DirectoryInfo(gameExeDir).Parent;

        if (gamePath?.Exists != true)
        {
            CustomMessageBox.Show("无法解析游戏目录, 注入失败", "XIVLauncherCN (Soil)", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: window);
            return false;
        }

        if (!EnsureDalamudCompatibility())
            return false;

        var dalamudSession = App.Dalamud.CreateLauncher
        (
            gamePath,
            new DalamudLaunchOptions
            (
                DalamudLoadMethod.EntryPoint,
                (int)App.Settings.DalamudInjectionDelayMS,
                false,
                noPlugins,
                noThird
            )
        );
        var dalamudOk = EnsureDalamudUpdate(dalamudSession, gamePath, true);

        Troubleshooting.LogTroubleshooting(gamePath);

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

        dalamudSession.InjectGame(gamePid, noPlugins);
        return true;
    }

    public bool EnsureDalamudCompatibility()
    {
        var dalamudCompatCheck = new DalamudCompatibilityCheck();

        try
        {
            dalamudCompatCheck.EnsureCompatibility();
            return true;
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
            return false;
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
            return false;
        }
    }

    private bool EnsureDalamudUpdate(DalamudSession dalamudSession, DirectoryInfo gamePath, bool appendWafStatusCodeHint)
    {
        try
        {
            App.Dalamud.RunUpdater();
            var dalamudStatus = dalamudSession.EnsureReady(gamePath);
            return dalamudStatus == DalamudSession.DalamudInstallState.Ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MainWindow] 尝试更新 Dalamud 时发生错误");

            var ensurementErrorMessage = "下载 Dalamud 相关文件异常\n请检查本地网络连接, 或关闭杀毒软件\n游戏将照常启动, 但无法使用 Dalamud";

            if (appendWafStatusCodeHint                                                        &&
                ex.FindHttpRequestException() is { StatusCode: not null } httpRequestException &&
                (int)httpRequestException.StatusCode is 403 or 444 or 522)
                ensurementErrorMessage = $"服务器错误: {httpRequestException.StatusCode}\n{httpRequestException.Message}\n{ensurementErrorMessage}";
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
