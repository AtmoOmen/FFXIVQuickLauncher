using XIVLauncher.Dalamud;
using XIVLauncher.Windows;

namespace XIVLauncher.Startup;

internal sealed class StartupDalamudProgressSink(LoadingDialog overlay) : IDalamudProgressSink
{
    public void ShowLoading() => overlay.ShowDialog();

    public void HideLoading() => overlay.HideDialog();

    public void SetLoadingMessage(string message) => overlay.SetMessage(message);

    public void ReportLoadingProgress(long? size, long downloaded, double? progress) => overlay.ReportProgress(size, downloaded, progress);
}
