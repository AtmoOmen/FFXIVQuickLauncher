using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XIVLauncher.Windows.ViewModel;

internal class LoadingDialogViewModel : INotifyPropertyChanged
{
    public string HeaderText
    {
        get;
        set => SetProperty(ref field, value);
    } = "正在准备更新...";

    public string DetailText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string PercentageText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public bool IsProgressBarVisible
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    public bool IsDetailTextVisible
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool IsPercentageTextVisible
    {
        get;
        set => SetProperty(ref field, value);
    }

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
