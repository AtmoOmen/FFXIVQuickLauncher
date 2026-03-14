using CheapLoc;

namespace XIVLauncher.Windows.ViewModel;

public class PatchDownloadDialogViewModel
{
    public string   PatchPreparingLoc           { get; private set; }
    public string   PatchGeneralStatusLoc       { get; private set; }
    public string   PatchCheckingLoc            { get; private set; }
    public string   PatchDoneLoc                { get; private set; }
    public string   PatchInstallingLoc          { get; private set; }
    public string   PatchInstallingFormattedLoc { get; private set; }
    public string   PatchInstallingIdleLoc      { get; private set; }
    public string   PatchEtaLoc                 { get; private set; }
    public string[] PatchEtaTimeLoc             { get; private set; }

    public PatchDownloadDialogViewModel() =>
        SetupLoc();

    private void SetupLoc()
    {
        PatchPreparingLoc           = Loc.Localize("PatchPreparing",           "Preparing...");
        PatchGeneralStatusLoc       = Loc.Localize("PatchGeneralStatus",       "Patching through {0} updates...");
        PatchCheckingLoc            = Loc.Localize("PatchChecking",            "Checking...");
        PatchDoneLoc                = Loc.Localize("PatchDone",                "Download done!");
        PatchInstallingLoc          = Loc.Localize("PatchInstalling",          "Installing...");
        PatchInstallingFormattedLoc = Loc.Localize("PatchInstallingFormatted", "Installing #{0}...");
        PatchInstallingIdleLoc      = Loc.Localize("PatchInstallingIdle",      "Waiting for download...");
        PatchEtaLoc                 = Loc.Localize("PatchEta",                 "{0} left to download at {1}/s.");
        PatchEtaTimeLoc =
        [
            Loc.Localize("PatchEtaTimeDays",    "预计剩余时间: {0} 天 {1} 小时 {2} 分 {3} 秒"),
            Loc.Localize("PatchEtaTimeHours",   "预计剩余时间: {0} 小时 {1} 分 {2} 秒"),
            Loc.Localize("PatchEtaTimeMinutes", "预计剩余时间: {0} 分 {1} 秒"),
            Loc.Localize("PatchEtaTimeSeconds", "预计剩余时间: {0} 秒")
        ];
    }
}
