using System;

namespace XIVLauncher.PatchInstaller.Utilities;

internal static class ThreadCountResolver
{
    public static int Resolve(int? requestedThreadCount)
    {
        var threadCount = requestedThreadCount ?? Math.Min(Environment.ProcessorCount, 8);
        if (threadCount < 0)
            throw new ArgumentOutOfRangeException(nameof(requestedThreadCount), "Must be 0 or more");

        return threadCount == 0 ? Environment.ProcessorCount : threadCount;
    }
}
