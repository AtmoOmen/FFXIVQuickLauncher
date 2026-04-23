using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XIVLauncher.Windows.ViewModel;

internal class IntegrityCheckProgressWindowViewModel : INotifyPropertyChanged
{
    public string CurrentFile
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;
        }
    } = string.Empty;

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
