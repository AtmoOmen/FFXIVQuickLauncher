using CommunityToolkit.Mvvm.ComponentModel;

namespace XIVLauncher.Windows.ViewModel;

internal partial class ChangeLogWindowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string UpdateNotice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VersionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ChangeLogText { get; set; } = "正在加载更新日志...";
}
