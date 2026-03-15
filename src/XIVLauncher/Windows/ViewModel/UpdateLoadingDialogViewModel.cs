using CheapLoc;

namespace XIVLauncher.Windows.ViewModel;

internal class UpdateLoadingDialogViewModel
{
    public string UpdateCheckLoc   { get; private set; }
    public string AutoLoginHintLoc { get; private set; }

    public UpdateLoadingDialogViewModel() =>
        SetupLoc();

    public void SetupLoc()
    {
        UpdateCheckLoc   = Loc.Localize("UpdateCheckMsg", "Checking for updates...");
        AutoLoginHintLoc = Loc.Localize("AutoLoginHint",  "Hold the shift key to change settings!");
    }
}
