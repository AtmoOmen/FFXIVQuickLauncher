using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XIVLauncher.Windows.ViewModel;

internal class ChangeLogWindowViewModel : INotifyPropertyChanged
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
