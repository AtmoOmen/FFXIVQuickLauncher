using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XIVLauncher.Windows.ViewModel;

public class PatchProgressItemViewModel : INotifyPropertyChanged
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
