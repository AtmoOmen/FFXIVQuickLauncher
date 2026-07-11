using CommunityToolkit.Mvvm.ComponentModel;

namespace XIVLauncher.Windows.ViewModel.Main.Models;

public partial class BannerDotInfo : ObservableObject
{
    [ObservableProperty]
    public partial bool Active { get; set; }

    public int Index { get; set; }
}
