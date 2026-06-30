using System.ComponentModel;
using System.Runtime.CompilerServices;
using XIVLauncher.Windows.GameClientFiles;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel;

public sealed class GameClientFileTaskWindowViewModel : INotifyPropertyChanged
{
    public SyncCommand PrimaryButtonCommand { get; }
    public SyncCommand SecondaryButtonCommand { get; }
    public SyncCommand CloseButtonCommand { get; }

    public GameClientFileTaskWindowViewModel()
    {
        PrimaryButtonCommand = new
        (
            _ => ActionRequested?.Invoke(GameClientFileTaskWindowAction.Primary),
            () => IsPrimaryButtonEnabled
        );
        SecondaryButtonCommand = new
        (
            _ => ActionRequested?.Invoke(GameClientFileTaskWindowAction.Secondary),
            () => IsSecondaryButtonEnabled
        );
        CloseButtonCommand = new
        (
            _ => ActionRequested?.Invoke(GameClientFileTaskWindowAction.Close),
            () => IsCloseButtonEnabled
        );
    }

    public Action<GameClientFileTaskWindowAction>? ActionRequested { get; set; }

    public string Title
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public string PhaseText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public string DetailText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public double Progress
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool IsProgressIndeterminate
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public string StatusText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public string SpeedText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public string EtaText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public IReadOnlyList<GameClientFileTaskItemSnapshot> Items
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    public string PrimaryButtonText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public bool IsPrimaryButtonVisible
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool IsPrimaryButtonEnabled
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value))
                return;

            PrimaryButtonCommand.RaiseCanExecuteChanged();
        }
    }

    public string SecondaryButtonText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public bool IsSecondaryButtonVisible
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool IsSecondaryButtonEnabled
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value))
                return;

            SecondaryButtonCommand.RaiseCanExecuteChanged();
        }
    }

    public string CloseButtonText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public bool IsCloseButtonVisible
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool IsCloseButtonEnabled
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value))
                return;

            CloseButtonCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsRunning
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public void ApplySnapshot(GameClientFileTaskSnapshot snapshot)
    {
        Title                    = snapshot.Title;
        PhaseText                = snapshot.PhaseText;
        DetailText               = snapshot.DetailText;
        Progress                 = snapshot.Progress;
        IsProgressIndeterminate  = snapshot.IsProgressIndeterminate;
        StatusText               = snapshot.StatusText;
        SpeedText                = snapshot.SpeedText;
        EtaText                  = snapshot.EtaText;
        Items                    = snapshot.Items;
        PrimaryButtonText        = snapshot.PrimaryButtonText;
        IsPrimaryButtonVisible   = snapshot.IsPrimaryButtonVisible;
        IsPrimaryButtonEnabled   = snapshot.IsPrimaryButtonEnabled;
        SecondaryButtonText      = snapshot.SecondaryButtonText;
        IsSecondaryButtonVisible = snapshot.IsSecondaryButtonVisible;
        IsSecondaryButtonEnabled = snapshot.IsSecondaryButtonEnabled;
        CloseButtonText          = snapshot.CloseButtonText;
        IsCloseButtonVisible     = snapshot.IsCloseButtonVisible;
        IsCloseButtonEnabled     = snapshot.IsCloseButtonEnabled;
        IsRunning                = snapshot.IsRunning;
    }

    public void RequestClose()
    {
        if (IsRunning && IsPrimaryButtonVisible && IsPrimaryButtonEnabled)
        {
            ActionRequested?.Invoke(GameClientFileTaskWindowAction.Primary);
            return;
        }

        ActionRequested?.Invoke(GameClientFileTaskWindowAction.Close);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
