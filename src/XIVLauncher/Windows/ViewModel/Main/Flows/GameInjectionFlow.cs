using System.Diagnostics;
using System.IO;
using System.Windows;
using XIVLauncher.Dalamud;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel.Main.Models;
using XIVLauncher.Windows.ViewModel.Main.Services;

namespace XIVLauncher.Windows.ViewModel.Main.Flows;

public sealed class GameInjectionFlow
(
    Window               window,
    DalamudLaunchService dalamudLaunchService
)
{
    public bool InjectGame
    (
        int  gamePid,
        bool noThird   = false,
        bool noPlugins = false
    )
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
        var gameExeDir = gameExePath == null ?
                             null :
                             Path.GetDirectoryName(gameExePath);
        var gamePath = gameExeDir == null ?
                           null :
                           new DirectoryInfo(gameExeDir).Parent;

        if (gamePath?.Exists != true)
        {
            CustomMessageBox.Show("无法解析游戏目录, 注入失败", "XIVLauncherCN (Soil)", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: window);
            return false;
        }

        if (!dalamudLaunchService.EnsureCompatibility())
            return false;

        var dalamudSession = dalamudLaunchService.CreateSession
        (
            gamePath,
            DalamudLoadMethod.EntryPoint,
            (int)App.Settings.DalamudInjectionDelayMS,
            noPlugins,
            noThird
        );
        var (dalamudUpdateResult, dalamudUpdateErrorMessage) = dalamudLaunchService.TryUpdate(dalamudSession, gamePath, true);

        Troubleshooting.LogTroubleshooting(gamePath);

        if (dalamudUpdateResult == DalamudPrepareResult.Failed)
        {
            if (dalamudUpdateErrorMessage != null)
            {
                CustomMessageBox.Builder
                                .NewFrom($"{dalamudUpdateErrorMessage}\n游戏将照常启动, 但无法使用 Dalamud")
                                .WithImage(MessageBoxImage.Warning)
                                .WithButtons(MessageBoxButton.OK)
                                .WithShowHelpLinks()
                                .WithParentWindow(window)
                                .Show();
            }

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
}
