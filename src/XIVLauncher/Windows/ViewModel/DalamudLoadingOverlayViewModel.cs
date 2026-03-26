namespace XIVLauncher.Windows.ViewModel;

internal class DalamudLoadingOverlayViewModel : ViewModelBase
{
    public string UpdateText
    {
        get;
        set => SetProperty(ref field, value);
    } = "正在更新 Dalamud 框架...";

    public string ProgressText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string PercentageText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public bool IsInfoVisible
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool IsProgressBarVisible
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    public bool IsProgressIndeterminate
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    public double ProgressValue
    {
        get;
        set => SetProperty(ref field, value);
    }
}
