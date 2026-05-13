using Downloader;
using Serilog;

namespace XIVLauncher.Common.Game.Patch.Acquisition;

internal class NetDownloaderPatchAcquisition : PatchAcquisition
{
    private readonly DownloadConfiguration _downloadOpt = new()
    {
        ParallelDownload = true,          // download parts of file as parallel or not
        BufferBlockSize  = 8000,          // usually, hosts support max to 8000 bytes
        ChunkCount       = 8,             // file parts to download
        MaxTryAgainOnFailure = int.MaxValue,
        BlockTimeout         = 10000,
        HttpClientTimeout    = 10000,
        RequestConfiguration = new RequestConfiguration
        {
            UserAgent = Constants.PatcherUserAgent,
            Accept    = "*/*"
        }
        //MaximumBytesPerSecond = App.Settings.SpeedLimitBytes / PatchManager.MAX_DOWNLOADS_AT_ONCE,
    };

    private DownloadService _dlService;

    public NetDownloaderPatchAcquisition(DirectoryInfo patchStore, long maxBytesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(patchStore);
    }

    public override async Task StartDownloadAsync(string url, FileInfo outFile)
    {
        _dlService = new DownloadService(_downloadOpt);

        _dlService.DownloadProgressChanged += (sender, args) =>
        {
            OnProgressChanged
            (
                new AcquisitionProgress
                {
                    BytesPerSecondSpeed = (long)args.BytesPerSecondSpeed,
                    Progress            = args.ReceivedBytesSize
                }
            );
        };

        _dlService.DownloadFileCompleted += (sender, args) =>
        {
            if (args.Error != null)
            {
                Log.Error(args.Error, "[WEB] Download failed for {0} with reason {1}", url, args.Error);

                // If we cancel downloads, we don't want to see an error message
                if (args.Error is OperationCanceledException)
                {
                    OnComplete(AcquisitionResult.Cancelled);
                    return;
                }

                OnComplete(AcquisitionResult.Error);
                return;
            }

            if (args.Cancelled)
            {
                Log.Error("[WEB] Download cancelled for {0} with reason {1}", url, args.Error);

                /*
                Cancellation should not produce an error message, since it is always triggered by another error or the user.
                */
                OnComplete(AcquisitionResult.Cancelled);
                return;
            }

            OnComplete(AcquisitionResult.Success);
        };

        await _dlService.DownloadFileTaskAsync(url, outFile.FullName);
    }

    public override async Task CancelAsync() =>
        _dlService.CancelAsync();
}
