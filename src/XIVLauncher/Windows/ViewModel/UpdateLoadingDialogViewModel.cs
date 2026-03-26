namespace XIVLauncher.Windows.ViewModel;

internal class UpdateLoadingDialogViewModel : ViewModelBase
{
    public string StatusText
    {
        get;
        set => SetProperty(ref field, value);
    } = "正在准备更新...";
}
