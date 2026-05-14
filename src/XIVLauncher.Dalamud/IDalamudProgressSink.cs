namespace XIVLauncher.Dalamud;

public interface IDalamudProgressSink
{
    void ShowLoading();

    void HideLoading();

    void SetLoadingMessage(string message);

    void ReportLoadingProgress(long? size, long downloaded, double? progress);
}
