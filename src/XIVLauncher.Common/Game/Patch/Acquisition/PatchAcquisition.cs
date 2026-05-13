namespace XIVLauncher.Common.Game.Patch.Acquisition;

public abstract class PatchAcquisition
{
    public abstract Task StartDownloadAsync(string url, FileInfo outFile);

    public abstract Task CancelAsync();

    protected void OnProgressChanged(AcquisitionProgress progress) =>
        ProgressChanged?.Invoke(this, progress);

    protected void OnComplete(AcquisitionResult result) =>
        Complete?.Invoke(this, result);

    public event EventHandler<AcquisitionProgress> ProgressChanged;

    public event EventHandler<AcquisitionResult> Complete;
}
