using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XIVLauncher.Windows.ViewModel;

public class PatchDownloadDialogViewModel : INotifyPropertyChanged
{
    public ObservableCollection<PatchProgressItemViewModel> ProgressItems { get; } =
    [
        new(),
        new(),
        new(),
        new()
    ];

    public string PatchProgressText
    {
        get;
        set => SetProperty(ref field, value);
    } = "正在准备更新...";

    public string InstallingText
    {
        get;
        set => SetProperty(ref field, value);
    } = "正在安装更新...";

    public string BytesLeftText
    {
        get;
        set => SetProperty(ref field, value);
    } = "正在准备更新...";

    public string TimeLeftText
    {
        get;
        set => SetProperty(ref field, value);
    } = "正在准备更新...";

    public void SetProgress(int index, string patchName, double percentage, bool indeterminate)
    {
        if (index < 0 || index >= ProgressItems.Count)
            return;

        ProgressItems[index].Title           = patchName;
        ProgressItems[index].Progress        = percentage;
        ProgressItems[index].IsIndeterminate = indeterminate;
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}
