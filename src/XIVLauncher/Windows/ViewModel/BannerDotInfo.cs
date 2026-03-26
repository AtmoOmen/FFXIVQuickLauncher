using System.ComponentModel;

namespace XIVLauncher.Windows.ViewModel;

public class BannerDotInfo : INotifyPropertyChanged
{
    private bool _active;

    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value)
                return;

            _active = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Active)));
        }
    }

    public int Index { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
