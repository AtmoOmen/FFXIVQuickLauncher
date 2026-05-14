namespace XIVLauncher.GamePatchV3.Integrity.Models;

public readonly record struct IntegrityPathEntry
(
    int    OriginalIndex,
    string DownloadPath,
    string CanonicalSdoPath,
    string GameRelativePath,
    string LocalRelativePath,
    string Hash,
    ulong  Size
)
{
    public static List<IntegrityPathEntry> BuildEntries(IntegrityCheckResult remoteIntegrity)
    {
        var selectedEntriesByCanonicalPath = new Dictionary<string, IntegrityPathEntry>(StringComparer.OrdinalIgnoreCase);
        var originalIndex                  = 0;

        foreach (var entry in remoteIntegrity.Hashes)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || !GamePathNormalizer.TryNormalizeGameRelativePath(entry.Key, out var gameRelativePath))
            {
                originalIndex++;
                continue;
            }

            var localRelativePath = gameRelativePath["game/".Length..];
            var candidate = new IntegrityPathEntry
            (
                originalIndex,
                GamePathNormalizer.NormalizeDownloadPath(entry.Key),
                GamePathNormalizer.ToCanonicalSdoPathFromGameRelativePath(gameRelativePath),
                gameRelativePath,
                localRelativePath,
                entry.Value,
                remoteIntegrity.Sizes is not null && remoteIntegrity.Sizes.TryGetValue(entry.Key, out var size) ? size : 0
            );

            if (selectedEntriesByCanonicalPath.TryGetValue(candidate.CanonicalSdoPath, out var existing))
                selectedEntriesByCanonicalPath[candidate.CanonicalSdoPath] = SelectPreferredEntry(existing, candidate);
            else
                selectedEntriesByCanonicalPath.Add(candidate.CanonicalSdoPath, candidate);

            originalIndex++;
        }

        return selectedEntriesByCanonicalPath.Values
                                             .OrderBy(x => x.OriginalIndex)
                                             .ToList();
    }

    private static IntegrityPathEntry SelectPreferredEntry(IntegrityPathEntry existing, IntegrityPathEntry candidate)
    {
        if (string.Equals(candidate.DownloadPath, candidate.CanonicalSdoPath, StringComparison.OrdinalIgnoreCase))
            return candidate;

        return existing;
    }
}
