namespace XIVLauncher.Windows.GameClientFiles;

public sealed class GameClientFileTaskSnapshot
{
    public string Title { get; init; } = string.Empty;

    public string PhaseText { get; init; } = string.Empty;

    public string DetailText { get; init; } = string.Empty;

    public double Progress { get; init; }

    public bool IsProgressIndeterminate { get; init; }

    public string StatusText { get; init; } = string.Empty;

    public string SpeedText { get; init; } = string.Empty;

    public string EtaText { get; init; } = string.Empty;

    public IReadOnlyList<GameClientFileTaskItemSnapshot> Items { get; init; } = [];

    public string PrimaryButtonText { get; init; } = string.Empty;

    public bool IsPrimaryButtonVisible { get; init; }

    public bool IsPrimaryButtonEnabled { get; init; }

    public string SecondaryButtonText { get; init; } = string.Empty;

    public bool IsSecondaryButtonVisible { get; init; }

    public bool IsSecondaryButtonEnabled { get; init; }

    public string CloseButtonText { get; init; } = string.Empty;

    public bool IsCloseButtonVisible { get; init; }

    public bool IsCloseButtonEnabled { get; init; }

    public bool IsRunning { get; init; }
}
