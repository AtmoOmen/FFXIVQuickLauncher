namespace XIVLauncher.Dalamud;

public sealed record DalamudStatusSnapshot
(
    DalamudUpdater.DownloadState State,
    string                       LoadingDetail,
    long?                        LoadingTotal,
    long                         LoadingDownloaded,
    double?                      LoadingProgress,
    string                       Version,
    string                       OnlineHash
);
