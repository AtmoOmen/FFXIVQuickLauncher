using CheapLoc;

namespace XIVLauncher.Windows.ViewModel;

internal class DalamudLoadingOverlayViewModel
{
    public string DalamudUpdateLoc { get; private set; }

    public DalamudLoadingOverlayViewModel() =>
        SetupLoc();

    public void SetupLoc() =>
        DalamudUpdateLoc = Loc.Localize("DalamudUpdate", "Updating Dalamud...");
}
