using System.Diagnostics;

namespace XIVLauncher.GamePatchV3;

internal sealed class TransferSpeedEstimator
{
    public long Speed => Interlocked.Read(ref speed);

    private long baselineTimestamp = Stopwatch.GetTimestamp();
    private long baselineProgress;
    private long speed;

    public void Reset
    (
        long progress = 0
    )
    {
        Interlocked.Exchange(ref baselineProgress,  progress);
        Interlocked.Exchange(ref baselineTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref speed,             0);
    }

    public void Update
    (
        long progress
    )
    {
        var now       = Stopwatch.GetTimestamp();
        var timestamp = Interlocked.Read(ref baselineTimestamp);
        var elapsed   = now - timestamp;
        if (elapsed < Stopwatch.Frequency)
            return;

        if (Interlocked.CompareExchange(ref baselineTimestamp, now, timestamp) != timestamp)
            return;

        var previousProgress = Interlocked.Exchange(ref baselineProgress, progress);
        var transferred      = progress - previousProgress;
        Interlocked.Exchange
        (
            ref speed,
            transferred <= 0 ?
                0 :
                (long)(transferred / (double)elapsed * Stopwatch.Frequency)
        );
    }
}
