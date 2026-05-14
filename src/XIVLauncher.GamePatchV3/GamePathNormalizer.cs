namespace XIVLauncher.GamePatchV3;

internal static class GamePathNormalizer
{
    public static bool TryNormalizeGameRelativePath(string path, out string gameRelativePath)
    {
        gameRelativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalizedPath = NormalizeRelativePath(path);
        if (!normalizedPath.StartsWith(GAME_PREFIX, StringComparison.OrdinalIgnoreCase))
            return false;

        gameRelativePath = normalizedPath;
        return true;
    }

    public static string NormalizeLocalRelativePath(string path) =>
        NormalizeRelativePath(path);

    public static string ToCanonicalSdoPathFromGameRelativePath(string gameRelativePath) =>
        "\\" + gameRelativePath.Replace('/', '\\');

    public static string ToCanonicalSdoPathFromLocalRelativePath(string localRelativePath) =>
        ToCanonicalSdoPathFromGameRelativePath($"{GAME_PREFIX}{NormalizeLocalRelativePath(localRelativePath)}");

    public static string CombineWithRootPath(string rootPath, string gameRelativePath) =>
        Path.Combine(rootPath, gameRelativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string NormalizeDownloadPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.StartsWith('\\') ? normalized : "\\" + normalized;
    }

    private static string NormalizeRelativePath(string path)
    {
        var segments = new List<string>();

        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);

                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    private const string GAME_PREFIX = "game/";
}
