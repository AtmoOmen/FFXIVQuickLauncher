using System.IO;
using System.Windows;
using Serilog;
using XIVLauncher.Common.Http;
using XIVLauncher.Dalamud;
using XIVLauncher.Windows.ViewModel.Main.Models;

namespace XIVLauncher.Windows.ViewModel.Main.Services;

public sealed class DalamudLaunchService
(
    Window window
)
{
    public DalamudSession CreateSession
    (
        DirectoryInfo     gamePath,
        DalamudLoadMethod loadMethod,
        int               injectionDelayMs,
        bool              noPlugins,
        bool              noThird
    ) =>
        App.Dalamud.CreateLauncher
        (
            gamePath,
            new DalamudLaunchOptions
            (
                loadMethod,
                injectionDelayMs,
                false,
                noPlugins,
                noThird
            )
        );

    public bool EnsureCompatibility()
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

    public (DalamudPrepareResult Result, string? ErrorMessage) TryUpdate
    (
        DalamudSession dalamudSession,
        DirectoryInfo  gamePath,
        bool           appendWafStatusCodeHint
    )
    {
        try
        {
            App.Dalamud.RunUpdater();
            var dalamudStatus = dalamudSession.EnsureReady(gamePath);
            return dalamudStatus == DalamudSession.DalamudInstallState.Ok ?
                       (Ok: DalamudPrepareResult.OK, null) :
                       (DalamudPrepareResult.Failed, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MainWindow] 尝试更新 Dalamud 时发生错误");

            var ensurementErrorMessage = "下载 Dalamud 相关文件异常\n请检查本地网络连接, 或关闭杀毒软件\n";

            if (appendWafStatusCodeHint                                                        &&
                ex.FindHttpRequestException() is { StatusCode: not null } httpRequestException &&
                (int)httpRequestException.StatusCode is 403 or 444 or 522)
                ensurementErrorMessage = $"服务器错误: {httpRequestException.StatusCode}\n{httpRequestException.Message}\n{ensurementErrorMessage}";
            else
                ensurementErrorMessage = $"错误: {ex.Message}\n{ensurementErrorMessage}";

            return (DalamudPrepareResult.Failed, ensurementErrorMessage);
        }
    }
}
