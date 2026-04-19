using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Util;
using XIVLauncher.Windows;

namespace XIVLauncher.Game;

internal static class ProblemCheck
{
    public static void RunCheck(Window parentWindow)
    {
        var procModules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>();

        if (procModules.Any(x => x.ModuleName == "MacType.dll" || x.ModuleName == "MacType64.dll"))
        {
            CustomMessageBox.Show
            (
                "检测到 MacType\n它会导致游戏出现问题, 无论是在官方启动器还是 XIVLauncher 中\n\n请将 XIVLauncher, ffxivboot, ffxivlauncher, ffxivupdater 和 ffxiv_dx11 从 MacType 中排除",
                "XIVLauncher Problem",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: parentWindow
            );
            Environment.Exit(-1);
        }

        if (!CheckMyGamesWriteAccess())
        {
            CustomMessageBox.Show
            (
                "没有权限写入游戏的 My Games 文件夹\n这将导致无法保存截图和部分角色数据\n\n这可能是由杀毒软件或权限错误引起的, 请检查 My Games 文件夹权限",
                "XIVLauncher Problem",
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                parentWindow: parentWindow
            );
        }

        if (App.Settings.GamePath == null)
            return;

        var gameFolderPath = Path.Combine(App.Settings.GamePath.FullName, "game");

        var d3d11   = new FileInfo(Path.Combine(gameFolderPath, "d3d11.dll"));
        var dxgi    = new FileInfo(Path.Combine(gameFolderPath, "dxgi.dll"));
        var dinput8 = new FileInfo(Path.Combine(gameFolderPath, "dinput8.dll"));

        if (!CheckSymlinkValid(d3d11) || !CheckSymlinkValid(dxgi) || !CheckSymlinkValid(dinput8))
        {
            if (CustomMessageBox.Builder
                                .NewFrom("GShade 符号链接已损坏\n\n游戏无法启动, 是否让 XIVLauncher 修复? 需要重新安装 GShade")
                                .WithButtons(MessageBoxButton.YesNo)
                                .WithImage(MessageBoxImage.Error)
                                .WithParentWindow(parentWindow)
                                .Show()
                == MessageBoxResult.Yes)
            {
                try
                {
                    if (d3d11.Exists)
                        ElevatedDelete(d3d11);

                    if (dxgi.Exists)
                        ElevatedDelete(dxgi);

                    if (dinput8.Exists)
                        ElevatedDelete(dinput8);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Could not delete broken GShade symlinks");
                }
            }
        }

        d3d11.Refresh();
        dinput8.Refresh();
        dxgi.Refresh();

        if (d3d11.Exists && dxgi.Exists)
        {
            var dxgiInfo  = FileVersionInfo.GetVersionInfo(dxgi.FullName);
            var d3d11Info = FileVersionInfo.GetVersionInfo(d3d11.FullName);

            if (dxgiInfo.ProductName?.Equals("GShade", StringComparison.OrdinalIgnoreCase) == true && d3d11Info.ProductName?.Equals("GShade", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (CustomMessageBox.Builder
                                    .NewFrom("检测到 GShade 安装损坏\n\n游戏无法启动, 是否让 XIVLauncher 修复? 需要重新安装 GShade")
                                    .WithButtons(MessageBoxButton.YesNo)
                                    .WithImage(MessageBoxImage.Error)
                                    .WithParentWindow(parentWindow)
                                    .Show()
                    == MessageBoxResult.Yes)
                {
                    try
                    {
                        ElevatedDelete(d3d11, dxgi);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Could not delete duplicate GShade");
                    }
                }
            }
        }

        d3d11.Refresh();
        dinput8.Refresh();
        dxgi.Refresh();

        if ((d3d11.Exists || dinput8.Exists) && !App.Settings.HasComplainedAboutGShadeDxgi.GetValueOrDefault(false))
        {
            FileVersionInfo? d3d11Info   = null;
            FileVersionInfo? dinput8Info = null;

            if (d3d11.Exists)
                d3d11Info = FileVersionInfo.GetVersionInfo(d3d11.FullName);

            if (dinput8.Exists)
                dinput8Info = FileVersionInfo.GetVersionInfo(dinput8.FullName);

            if ((d3d11Info?.ProductName?.Equals("GShade", StringComparison.OrdinalIgnoreCase) ?? false) || (dinput8Info?.ProductName?.Equals("GShade", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                if (CustomMessageBox.Builder
                                    .NewFrom("GShade 安装模式不适合与 XIVLauncher 一起使用, 是否让 XIVLauncher 修复?\n\n这不会更改预设或设置, 只是提高与 XIVLauncher 功能的兼容性")
                                    .WithButtons(MessageBoxButton.YesNo)
                                    .WithImage(MessageBoxImage.Warning)
                                    .WithParentWindow(parentWindow)
                                    .Show()
                    == MessageBoxResult.Yes)
                {
                    try
                    {
                        var toMove = d3d11.Exists ? d3d11 : dinput8;

                        var psi = new ProcessStartInfo
                        {
                            Verb             = "runas",
                            FileName         = GetCmdPath(),
                            WorkingDirectory = Paths.ResourcesPath,
                            Arguments        = $"/C \"move \"{Path.Combine(gameFolderPath, toMove.Name)}\" \"{Path.Combine(gameFolderPath, "dxgi.dll")}\"\"",
                            UseShellExecute  = true,
                            CreateNoWindow   = true,
                            WindowStyle      = ProcessWindowStyle.Hidden
                        };

                        var process = Process.Start(psi);

                        if (process == null)
                            throw new Exception("Could not spawn CMD when fixing GShade");

                        process.WaitForExit();

                        var gshadeInstKey = Registry.LocalMachine.OpenSubKey
                        (
                            "SOFTWARE\\GShade\\Installations",
                            false
                        );

                        if (gshadeInstKey != null)
                        {
                            var gshadeInstSubKeys = gshadeInstKey.GetSubKeyNames();

                            var gshadeInstsToFix = new Stack<string>();

                            foreach (var gshadeInst in gshadeInstSubKeys)
                            {
                                if (gshadeInst.Contains("ffxiv_dx11.exe"))
                                    gshadeInstsToFix.Push(gshadeInst);
                            }

                            if (gshadeInstsToFix.Count > 0)
                            {
                                while (gshadeInstsToFix.Count > 0)
                                {
                                    var gshadePsi = new ProcessStartInfo
                                    {
                                        Verb             = "runas",
                                        FileName         = "reg.exe",
                                        WorkingDirectory = Environment.SystemDirectory,
                                        Arguments        = $"add \"HKLM\\SOFTWARE\\GShade\\Installations\\{gshadeInstsToFix.Pop()}\" /v \"altdxmode\" /t \"REG_SZ\" /d \"0\" /f",
                                        UseShellExecute  = true,
                                        CreateNoWindow   = true,
                                        WindowStyle      = ProcessWindowStyle.Hidden
                                    };

                                    var gshadeProcess = Process.Start(gshadePsi);

                                    if (gshadeProcess == null)
                                        throw new Exception("Could not spawn reg when fixing GShade");

                                    gshadeProcess.WaitForExit();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Could not fix GShade incompatibility");
                    }
                }
                else
                    App.Settings.HasComplainedAboutGShadeDxgi = true;
            }
        }
    }

    private static string GetCmdPath() => Path.Combine(Environment.ExpandEnvironmentVariables("%WINDIR%"), "System32", "cmd.exe");

    private static void ElevatedDelete(params FileInfo[] info)
    {
        var pathsToDelete = info.Select(x => $"\"{x.FullName}\"").Aggregate("", (current, name) => current + $"{name} ");

        var psi = new ProcessStartInfo
        {
            Verb            = "runas",
            FileName        = GetCmdPath(),
            Arguments       = $"/C \"del {pathsToDelete}\"",
            UseShellExecute = true,
            CreateNoWindow  = true,
            WindowStyle     = ProcessWindowStyle.Hidden
        };

        var process = Process.Start(psi);

        if (process == null)
            throw new Exception("Could not spawn CMD for elevated delete");

        process.WaitForExit();
    }

    private static bool CheckMyGamesWriteAccess()
    {
        // Create a randomly-named file in the game's user data folder and make sure we don't
        // get a permissions error.
        var myGames = Path.Combine(App.Settings.GamePath.FullName, "my games");
        if (!Directory.Exists(myGames))
            return true;

        // var targetPath = Directory.GetDirectories(myGames).FirstOrDefault(x => Path.GetDirectoryName(x)?.Length == 34);
        // if (targetPath == null)
        //     return true;
        var targetPath = myGames;
        var tempFile   = Path.Combine(targetPath, Guid.NewGuid().ToString());

        try
        {
            var file = File.Create(tempFile);
            file.Dispose();
            File.Delete(tempFile);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception)
        {
            return true;
        }

        return true;
    }

    private static bool CheckSymlinkValid(FileInfo file)
    {
        if (!file.Exists)
            return true;

        try
        {
            file.OpenRead();
        }
        catch (IOException)
        {
            return false;
        }

        return true;
    }
}
