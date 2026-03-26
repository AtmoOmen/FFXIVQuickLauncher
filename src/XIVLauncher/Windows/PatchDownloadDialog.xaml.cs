using System;
using System.ComponentModel;
using System.Linq;
using System.Timers;
using System.Windows;
using System.Windows.Input;
using XIVLauncher.Common.Game.Patch;
using XIVLauncher.Common.Util;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for PatchDownloadDialog.xaml
/// </summary>
public partial class PatchDownloadDialog : Window
{
    public           PatchDownloadDialogViewModel ViewModel => (PatchDownloadDialogViewModel)DataContext;
    private readonly PatchManager                 _manager;

    private readonly Timer _timer;

    public PatchDownloadDialog(PatchManager manager)
    {
        InitializeComponent();

        _manager = manager;

        DataContext = new PatchDownloadDialogViewModel();

        MouseMove += PatchDownloadDialog_OnMouseMove;

        _timer           =  new Timer();
        _timer.Elapsed   += ViewUpdateTimerOnElapsed;
        _timer.AutoReset =  true;
        _timer.Interval  =  200;

        IsVisibleChanged += (_, _) => _timer.Enabled = IsVisible;
        Closed           += (_, _) => _timer.Dispose();
    }

    public void SetGeneralProgress(int curr, int final, bool busy)
    {
        ViewModel.PatchProgressText = $"正在更新第 {curr}/{final} 个补丁...";
        ViewModel.InstallingText    = busy ? $"正在安装第 #{curr} 个更新..." : "正在等待下载...";
    }

    public void SetLeft(long left, double rate)
    {
        var eta = rate == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(left / rate);
        ViewModel.BytesLeftText = $"剩余 {APIHelper.BytesToString(left)} (下载速度: {APIHelper.BytesToString(rate)}/s)";
        ViewModel.TimeLeftText  = APIHelper.GetTimeLeft(eta, ["预计剩余时间: {0} 天 {1} 小时 {2} 分 {3} 秒", "预计剩余时间: {0} 小时 {1} 分 {2} 秒", "预计剩余时间: {0} 分 {1} 秒", "预计剩余时间: {0} 秒"]);
    }

    public void SetPatchProgress(int index, string patchName, double pct, bool indeterminate) =>
        ViewModel.SetProgress(index, patchName, pct, indeterminate);

    public void SetProgressBar1Progress(string patchName, double percentage, bool indeterminate) =>
        ViewModel.SetProgress(0, patchName, percentage, indeterminate);

    public void SetProgressBar2Progress(string patchName, double percentage, bool indeterminate) =>
        ViewModel.SetProgress(1, patchName, percentage, indeterminate);

    public void SetProgressBar3Progress(string patchName, double percentage, bool indeterminate) =>
        ViewModel.SetProgress(2, patchName, percentage, indeterminate);

    public void SetProgressBar4Progress(string patchName, double percentage, bool indeterminate) =>
        ViewModel.SetProgress(3, patchName, percentage, indeterminate);

    private void PatchDownloadDialog_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void ViewUpdateTimerOnElapsed(object sender, ElapsedEventArgs e)
    {
        if (_manager == null)
            return;

        Dispatcher.Invoke
        (() =>
            {
                SetGeneralProgress(_manager.CurrentInstallIndex, _manager.Downloads.Count, _manager.IsInstallerBusy);

                for (var i = 0; i < PatchManager.MAX_DOWNLOADS_AT_ONCE; i++)
                {
                    var activePatch = _manager.Actives[i];

                    if (_manager.Slots[i] == PatchManager.SlotState.Done || activePatch == null)
                    {
                        SetPatchProgress(i, "更新下载完成", 100f, false);
                        continue;
                    }

                    if (_manager.Slots[i] == PatchManager.SlotState.Checking)
                        SetPatchProgress(i, $"{activePatch.Patch} (正在检查更新...)", 100f, true);
                    else
                    {
                        var pct = Math.Round((double)(100 * _manager.Progresses[i]) / activePatch.Patch.Length, 2);
                        SetPatchProgress(i, $"{activePatch.Patch} ({pct:#0.0}%, {APIHelper.BytesToString(_manager.Speeds[i])}/s)", pct, false);
                    }
                }

                if (_manager.DownloadsDone)
                    SetLeft(0, 0);
                else
                    SetLeft(_manager.AllDownloadsLength < 0 ? 0 : _manager.AllDownloadsLength, _manager.Speeds.Sum());
            }
        );
    }

    private void PatchDownloadDialog_OnClosing(object sender, CancelEventArgs e) =>
        e.Cancel = true; // We can't cancel patching yet, big TODO
}
