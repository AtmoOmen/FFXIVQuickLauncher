using CommunityToolkit.Mvvm.ComponentModel;

namespace XIVLauncher.Windows.ViewModel;

internal partial class LoadingDialogViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string HeaderText { get; set; } = "正在准备更新...";

    [ObservableProperty]
    public partial string DetailText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PercentageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsProgressBarVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsDetailTextVisible { get; set; }

    [ObservableProperty]
    public partial bool IsPercentageTextVisible { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; } = true;

    [ObservableProperty]
    public partial double ProgressValue { get; set; }
}
