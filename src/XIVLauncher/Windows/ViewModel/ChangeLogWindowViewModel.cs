namespace XIVLauncher.Windows.ViewModel;

internal class ChangeLogWindowViewModel : ViewModelBase
{
    public string UpdateNotice
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string ChangeLogText
    {
        get;
        set => SetProperty(ref field, value);
    } = "正在加载更新日志...";
}
