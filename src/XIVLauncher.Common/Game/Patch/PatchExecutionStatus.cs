namespace XIVLauncher.Common.Game.Patch;

public enum PatchExecutionStatus
{
    Success,
    AlreadyRunning,
    CancelledByUser,
    PatchInstallerError,
    NotEnoughSpace,
    Failed
}
