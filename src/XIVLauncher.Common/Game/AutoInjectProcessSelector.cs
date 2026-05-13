namespace XIVLauncher.Common.Game;

public static class AutoInjectProcessSelector
{
    public static void CleanupAttemptedProcessIds(IEnumerable<FFXIVProcess> processes, HashSet<int> attemptedProcessIds)
    {
        var activeProcessIds = processes.Select(process => process.ProcessID).ToHashSet();
        attemptedProcessIds.RemoveWhere(processId => !activeProcessIds.Contains(processId));
    }

    public static FFXIVProcess? FindNextCandidate(IEnumerable<FFXIVProcess> processes, IReadOnlySet<int> attemptedProcessIds) =>
        processes.FirstOrDefault(process => !process.HasInjected && !attemptedProcessIds.Contains(process.ProcessID));
}
