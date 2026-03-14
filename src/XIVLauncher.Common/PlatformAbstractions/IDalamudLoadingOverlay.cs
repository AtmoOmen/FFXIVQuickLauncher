namespace XIVLauncher.Common.PlatformAbstractions;

public interface IDalamudLoadingOverlay
{
    void SetStep(DalamudUpdateStep step);

    void SetVisible();

    void SetInvisible();

    void ReportProgress(long? size, long downloaded, double? progress);

    enum DalamudUpdateStep
    {
        Dalamud,
        Assets,
        Runtime,
        Unavailable,
        Starting
    }
}
