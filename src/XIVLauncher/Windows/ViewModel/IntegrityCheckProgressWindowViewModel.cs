using CheapLoc;

namespace XIVLauncher.Windows.ViewModel;

internal class IntegrityCheckProgressWindowViewModel
{
    public string IntegrityCheckRunningLoc { get; private set; }

    public IntegrityCheckProgressWindowViewModel() =>
        SetupLoc();

    private void SetupLoc() =>
        IntegrityCheckRunningLoc = Loc.Localize("IntegrityCheckRunning", "Running integrity check...");
}
