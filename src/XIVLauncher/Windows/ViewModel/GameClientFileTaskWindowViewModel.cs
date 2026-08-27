using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVLauncher.Windows.GameClientFiles;

namespace XIVLauncher.Windows.ViewModel;

public sealed partial class GameClientFileTaskWindowViewModel : ObservableObject
{
    public Action<GameClientFileTaskWindowAction>? ActionRequested { get; set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PhaseText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SpeedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string EtaText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<GameClientFileTaskItemSnapshot> Items { get; private set; } = [];

    [ObservableProperty]
    public partial string PrimaryButtonText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrimaryButtonCommand))]
    public partial bool IsPrimaryButtonVisible { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrimaryButtonCommand))]
    public partial bool IsPrimaryButtonEnabled { get; private set; }

    [ObservableProperty]
    public partial string SecondaryButtonText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSecondaryButtonVisible { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SecondaryButtonCommand))]
    public partial bool IsSecondaryButtonEnabled { get; private set; }

    [ObservableProperty]
    public partial string CloseButtonText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCloseButtonVisible { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseButtonCommand))]
    public partial bool IsCloseButtonEnabled { get; private set; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [RelayCommand(CanExecute = nameof(CanPrimaryButton))]
    private void PrimaryButton() =>
        ActionRequested?.Invoke(GameClientFileTaskWindowAction.Primary);

    private bool CanPrimaryButton() => IsPrimaryButtonEnabled;

    [RelayCommand(CanExecute = nameof(CanSecondaryButton))]
    private void SecondaryButton() =>
        ActionRequested?.Invoke(GameClientFileTaskWindowAction.Secondary);

    private bool CanSecondaryButton() => IsSecondaryButtonEnabled;

    [RelayCommand(CanExecute = nameof(CanCloseButton))]
    private void CloseButton() =>
        ActionRequested?.Invoke(GameClientFileTaskWindowAction.Close);

    private bool CanCloseButton() => IsCloseButtonEnabled;

    public void ApplySnapshot
    (
        GameClientFileTaskSnapshot snapshot
    )
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
}
