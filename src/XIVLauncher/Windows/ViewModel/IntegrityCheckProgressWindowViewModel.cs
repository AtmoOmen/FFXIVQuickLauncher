namespace XIVLauncher.Windows.ViewModel;

internal class IntegrityCheckProgressWindowViewModel : ViewModelBase
{
    public string CurrentFile
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;
}
