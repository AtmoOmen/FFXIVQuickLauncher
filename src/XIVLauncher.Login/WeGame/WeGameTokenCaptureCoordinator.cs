using Serilog;
using XIVLauncher.Login.Workflow;

namespace XIVLauncher.Login.WeGame;

public sealed class WeGameTokenCaptureCoordinator : IWeGameTokenCaptureCoordinator
{
    public async Task<WeGameCaptureResult?> CaptureAsync
    (
        ILoginWorkflowUI interaction,
        CancellationTokenSource   loginCancellationTokenSource
    )
    {
        var sdologinDir = await EnsureWeGamePathAsync(interaction).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sdologinDir))
            return null;

        var progress = new Progress<string>(interaction.ShowLoginMessage);

        while (true)
        {
            try
            {
                var capturer = new WeGameLoginCapturer();
                return await capturer.CaptureAsync(sdologinDir, loginCancellationTokenSource.Token, progress).ConfigureAwait(false);
            }
            catch (VersionDllPermissionDeniedException ex)
            {
                var copied = await interaction.TryElevatedCopyVersionDllAsync(ex.SourcePath, ex.DestinationPath, loginCancellationTokenSource.Token).ConfigureAwait(false);
                if (!copied)
                    return null;
            }
            catch (WeGameCapturePipeBusyException)
            {
                interaction.ShowError("命名管道 ApkalluCaller 已被另一个进程占用, 请确认是否同时启动了多个 XIVLauncherCN 后重试");
                return null;
            }
            catch (Exception ex) when (loginCancellationTokenSource.IsCancellationRequested)
            {
                Log.Information(ex, "手动取消了 WeGame 登录");
                return null;
            }
        }
    }

    private static Task<string?> EnsureWeGamePathAsync(ILoginWorkflowUI interaction)
    {
        var currentPath = interaction.GetSavedWeGamePath();
        if (WeGamePathValidator.IsValidGameRoot(currentPath))
        {
            var currentSdologinDir = WeGamePathValidator.DeriveSdologinDir(currentPath!);
            if (WeGamePathValidator.IsValidSdologinDir(currentSdologinDir))
                return Task.FromResult<string?>(currentSdologinDir);
        }

        var selectedRoot = interaction.PromptWeGameInstallDirectory(currentPath);
        if (string.IsNullOrWhiteSpace(selectedRoot))
            return Task.FromResult<string?>(null);

        if (!WeGamePathValidator.IsValidGameRoot(selectedRoot))
        {
            interaction.ShowError("未识别为 WeGame 版最终幻想 14 安装目录, 请确认所选路径");
            return Task.FromResult<string?>(null);
        }

        var sdologinDir = WeGamePathValidator.DeriveSdologinDir(selectedRoot);
        if (!WeGamePathValidator.IsValidSdologinDir(sdologinDir))
        {
            interaction.ShowError($"未在 {sdologinDir} 下找到 sdologin.exe, 请确认所选路径完整");
            return Task.FromResult<string?>(null);
        }

        interaction.SaveWeGamePath(selectedRoot);
        return Task.FromResult<string?>(sdologinDir);
    }
}
