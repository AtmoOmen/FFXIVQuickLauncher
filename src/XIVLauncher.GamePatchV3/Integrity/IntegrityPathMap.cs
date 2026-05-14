using XIVLauncher.GamePatchV3.Models;

namespace XIVLauncher.GamePatchV3.Integrity;

internal readonly record struct IntegrityPathEntry
(
    int    OriginalIndex,
    string DownloadPath,
    string CanonicalSdoPath,
    string GameRelativePath,
    string LocalRelativePath,
    string Hash,
    ulong  Size
);

internal static class IntegrityPathMap
{
    public static List<IntegrityPathEntry> BuildEntries(IntegrityCheckResult remoteIntegrity)
    {
        var selectedEntriesByCanonicalPath = new Dictionary<string, IntegrityPathEntry>(StringComparer.OrdinalIgnoreCase);
        var originalIndex                  = 0;

        foreach (var entry in remoteIntegrity.Hashes)
        {
            if (!TryCreateEntry(originalIndex, entry.Key, entry.Value, remoteIntegrity.Sizes is not null && remoteIntegrity.Sizes.TryGetValue(entry.Key, out var size) ? size : 0, out var candidate))
            {
                originalIndex++;
                continue;
            }

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

    private static bool TryCreateEntry(int originalIndex, string path, string hash, ulong size, out IntegrityPathEntry entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(path) || !GamePathNormalizer.TryNormalizeGameRelativePath(path, out var gameRelativePath))
            return false;

        var localRelativePath = gameRelativePath["game/".Length..];
        entry = new IntegrityPathEntry
        (
            originalIndex,
            GamePathNormalizer.NormalizeDownloadPath(path),
            GamePathNormalizer.ToCanonicalSdoPathFromGameRelativePath(gameRelativePath),
            gameRelativePath,
            localRelativePath,
            hash,
            size
        );
        return true;
    }

    private static IntegrityPathEntry SelectPreferredEntry(IntegrityPathEntry existing, IntegrityPathEntry candidate)
    {
        if (string.Equals(candidate.DownloadPath, candidate.CanonicalSdoPath, StringComparison.OrdinalIgnoreCase))
            return candidate;

        return existing;
    }
}
