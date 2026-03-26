using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XIVLauncher.Windows.ViewModel;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}
