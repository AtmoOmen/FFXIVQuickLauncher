using System.ComponentModel;

namespace XIVLauncher.Windows.ViewModel;

public class BannerDotInfo : INotifyPropertyChanged
{
    public bool Active
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Active)));
        }
    }

    public int Index { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
