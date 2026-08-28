using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVLauncher.Dalamud;

namespace XIVLauncher.Windows.ViewModel.Main;

/// <summary>
///     Dalamud 更新状态展示与手动刷新命令
/// </summary>
internal sealed partial class DalamudStatusViewModel : ObservableObject
{
    private readonly SettingsWindowViewModel settings;
    private readonly Window                  window;

    public DalamudStatusViewModel
    (
        Window                  window,
        SettingsWindowViewModel settings
    )
    {
        this.window   = window;
        this.settings = settings;

        App.Dalamud.StatusChanged += OnStatusChanged;
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [RelayCommand(CanExecute = nameof(CanRefreshDalamudInfo))]
    private void RefreshDalamudInfo() =>
        App.Dalamud.RunUpdater(true);

    private bool CanRefreshDalamudInfo() =>
        settings.EnableHooks && App.Dalamud.Updater.State != DalamudUpdater.DownloadState.Unknown;

    public void Detach() =>
        App.Dalamud.StatusChanged -= OnStatusChanged;

    public void RefreshStatus() =>
        UpdateStatusText();

    public void RefreshCommandState() =>
        RefreshDalamudInfoCommand.NotifyCanExecuteChanged();

    private void OnStatusChanged(DalamudStatusSnapshot _)
    {
        if (window.Dispatcher == Dispatcher.CurrentDispatcher)
        {
            UpdateStatusText();
            return;
        }

        window.Dispatcher.Invoke(UpdateStatusText);
    }

    private void UpdateStatusText()
    {
        var updater = App.Dalamud.GetStatusSnapshot();

        StatusText = updater.State switch
        {
            DalamudUpdater.DownloadState.Done => string.IsNullOrWhiteSpace(DalamudUpdater.Version) ?
                                                     "Dalamud 已就绪" :
                                                     $"Dalamud {DalamudUpdater.Version}",
            DalamudUpdater.DownloadState.NoIntegrity => "Dalamud 加载失败",
            _                                        => GetLoadingText(updater)
        };

        RefreshDalamudInfoCommand.NotifyCanExecuteChanged();
    }

    private static string GetLoadingText(DalamudStatusSnapshot updater)
    {
        if (updater.LoadingProgress is { } progress)
            return $"Dalamud 正在加载 {progress.ToString("0.##", CultureInfo.InvariantCulture)}%";

        if (!string.IsNullOrWhiteSpace(updater.LoadingDetail))
            return $"Dalamud {updater.LoadingDetail.TrimEnd('.')}";

        return "Dalamud 正在加载";
    }
}
