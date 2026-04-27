using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Windows;

namespace XIVLauncher.Startup.Steps;

public class DalamudInitStep
(
    UpdateCheckStep updateCheckStep
) : IStartupStep
{
    public string Name  => "Dalamud 初始化";
    public int    Order => 90;

    public Task ExecuteAsync(StartupContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            context.DalamudUpdater = new DalamudUpdater
            (
                new(Path.Combine(Paths.RoamingPath, "addon")),
                new(Path.Combine(Paths.RoamingPath, "runtime")),
                new(Path.Combine(Paths.RoamingPath, "dalamudAssets")),
                context.Settings.GitHubToken
            );

            if (updateCheckStep.DalamudRunnerOverride != null)
                context.DalamudUpdater.RunnerOverride = updateCheckStep.DalamudRunnerOverride;

            var dalamudWindowThread = new Thread(() => StartDalamudOverlayThread(context));
            dalamudWindowThread.SetApartmentState(ApartmentState.STA);
            dalamudWindowThread.IsBackground = true;
            dalamudWindowThread.Start();

            while (context.DalamudUpdater.ShowLoadingCallback == null)
                Thread.Yield();

            context.DalamudUpdater.Run();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法启动 Dalamud 更新器");
            throw;
        }

        return Task.CompletedTask;
    }

    private static void StartDalamudOverlayThread(StartupContext context)
    {
        var overlay = new LoadingDialog("正在更新 Dalamud 框架...", true);
        overlay.Hide();
        context.DalamudUpdater.ShowLoadingCallback = overlay.ShowDialog;
        context.DalamudUpdater.HideLoadingCallback = overlay.HideDialog;
        context.DalamudUpdater.SetLoadingMessage = overlay.SetMessage;
        context.DalamudUpdater.ReportLoadingProgress = overlay.ReportProgress;

        Dispatcher.Run();
    }
}
