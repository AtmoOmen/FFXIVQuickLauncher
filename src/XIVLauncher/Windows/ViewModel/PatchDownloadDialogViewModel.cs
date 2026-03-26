using System.Collections.ObjectModel;

namespace XIVLauncher.Windows.ViewModel;

public class PatchDownloadDialogViewModel : ViewModelBase
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
}

public class PatchProgressItemViewModel : ViewModelBase
{
    public string Title
    {
        get;
        set => SetProperty(ref field, value);
    } = "更新下载完成";

    public double Progress
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool IsIndeterminate
    {
        get;
        set => SetProperty(ref field, value);
    }
}
