using System.Collections.Concurrent;
using System.Security.Cryptography;
using XIVLauncher.Common;

namespace XIVLauncher.GamePatchV3;

public static class GameIntegrityChecker
{
    public static async Task<IntegrityCheckCompareOutcome> CompareIntegrityAsync
    (
        IProgress<IntegrityCheckProgress>? progress,
        DirectoryInfo                     gamePath,
        bool                              onlyIndex = false,
        CancellationToken                 cancellationToken = default
    )
    {
        IntegrityCheckResult remoteIntegrity;

        try
        {
            remoteIntegrity = await DownloadIntegrityCheckForVersion(cancellationToken).ConfigureAwait(false);
            var localVersion = Repository.Ffxiv.GetVer(gamePath);
            if (!string.Equals(localVersion, remoteIntegrity.GameVersion, StringComparison.Ordinal))
                return new IntegrityCheckCompareOutcome { CompareResult = IntegrityCheckCompareResult.ReferenceNotFound };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new IntegrityCheckCompareOutcome { CompareResult = IntegrityCheckCompareResult.ReferenceFetchFailure };
        }

        var localIntegrity = await RunIntegrityCheckAsync(gamePath, progress, onlyIndex, cancellationToken).ConfigureAwait(false);
        var report         = string.Empty;
        var failed         = false;

        foreach (var hashEntry in remoteIntegrity.Hashes
                                                 .Where
                                                 (hashEntry => !onlyIndex
                                                               || hashEntry.Key.EndsWith(".index",  StringComparison.Ordinal)
                                                               || hashEntry.Key.EndsWith(".index2", StringComparison.Ordinal)
                                                 ))
        {
            if (localIntegrity.Hashes.TryGetValue(hashEntry.Key, out var localHash))
            {
                if (remoteIntegrity.Sizes.TryGetValue(hashEntry.Key, out var remoteSize)
                    && localIntegrity.Sizes.TryGetValue(hashEntry.Key, out var localSize)
                    && localSize != remoteSize)
                {
                    report += $"Size mismatch: {hashEntry.Key}\n";
                    failed =  true;
                    continue;
                }

                if (!string.Equals(localHash, hashEntry.Value, StringComparison.OrdinalIgnoreCase))
                {
                    report += $"Mismatch: {hashEntry.Key}\n";
                    failed =  true;
                }
            }
            else
            {
                report += $"Missing: {hashEntry.Key}\n";
                failed =  true;
            }
        }

        return new IntegrityCheckCompareOutcome
        {
            CompareResult  = failed ? IntegrityCheckCompareResult.Invalid : IntegrityCheckCompareResult.Valid,
            Report         = report,
            RemoteIntegrity = remoteIntegrity
        };
    }

    public static async Task<Dictionary<string, VersionMappingEntry>> GetVersionMapping(CancellationToken cancellationToken = default)
    {
        using var metadataClient = new GamePatchMetadataClient();
        return await metadataClient.DownloadVersionMapping(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> GetFileMd5Hash(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       md5    = MD5.Create();
        var             hash   = await md5.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public static async Task<IntegrityCheckResult> DownloadIntegrityCheckForVersion(CancellationToken cancellationToken = default)
    {
        using var metadataClient = new GamePatchMetadataClient();
        return await metadataClient.DownloadIntegrityCheck(cancellationToken).ConfigureAwait(false);
    }

    public static Task<IntegrityCheckResult> RunIntegrityCheckAsync
    (
        DirectoryInfo                      gamePath,
        IProgress<IntegrityCheckProgress>? progress,
        bool                               onlyIndex = false,
        CancellationToken                  cancellationToken = default
    )
    {
        var files = CheckDirectory(gamePath, gamePath.FullName, progress, onlyIndex, cancellationToken);
        return Task.FromResult
        (
            new IntegrityCheckResult
            {
                GameVersion = Repository.Ffxiv.GetVer(gamePath),
                Hashes      = files.ToDictionary(x => x.Key, x => x.Value.Hash),
                Sizes       = files.ToDictionary(x => x.Key, x => x.Value.Size)
            }
        );
    }

    private static ConcurrentDictionary<string, (string Hash, ulong Size)> CheckDirectory
    (
        DirectoryInfo                      directory,
        string                             rootDirectory,
        IProgress<IntegrityCheckProgress>? progress,
        bool                               onlyIndex,
        CancellationToken                  cancellationToken
    )
    {
        var filesToProcess = new List<FileInfo>();
        CollectFiles(directory, rootDirectory, onlyIndex, filesToProcess);

        var results = new ConcurrentDictionary<string, (string Hash, ulong Size)>();
        var processedFileCount = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Math.Max(Environment.ProcessorCount - 2, 1), 32),
            CancellationToken      = cancellationToken
        };

        Parallel.ForEach
        (
            filesToProcess,
            options,
            file =>
            {
                try
                {
                    var relativePath = GetRelativePath(file.FullName, rootDirectory);

                    using var md5 = MD5.Create();
                    using var stream = new BufferedStream
                    (
                        file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                        1200000
                    );

                    var hash       = md5.ComputeHash(stream);
                    var hashString = Convert.ToHexString(hash);

                    results.TryAdd(relativePath, (hashString, (ulong)file.Length));
                    progress?.Report
                    (
                        new IntegrityCheckProgress
                        {
                            CurrentFile        = relativePath,
                            ProcessedFileCount = Interlocked.Increment(ref processedFileCount),
                            TotalFileCount     = filesToProcess.Count,
                            PhaseText          = "正在检查游戏文件完整性"
                        }
                    );
                }
                catch (IOException)
                {
                    // Ignore.
                }
            }
        );

        return results;
    }

    private static void CollectFiles
    (
        DirectoryInfo  directory,
        string         rootDirectory,
        bool           onlyIndex,
        List<FileInfo> filesToProcess
    )
    {
        foreach (var file in directory.GetFiles())
        {
            var relativePath = GetRelativePath(file.FullName, rootDirectory);

            if (!relativePath.StartsWith("\\game", StringComparison.Ordinal))
                continue;

            if (relativePath.StartsWith("\\game\\My Games", StringComparison.Ordinal))
                continue;

            if (onlyIndex && !relativePath.EndsWith(".index", StringComparison.Ordinal) && !relativePath.EndsWith(".index2", StringComparison.Ordinal))
                continue;

            filesToProcess.Add(file);
        }

        foreach (var dir in directory.GetDirectories())
        {
            if (!dir.FullName.ToLowerInvariant().Contains("shade", StringComparison.Ordinal))
                CollectFiles(dir, rootDirectory, onlyIndex, filesToProcess);
        }
    }

    private static string GetRelativePath(string fullPath, string rootDirectory)
    {
        var relative = fullPath[rootDirectory.Length..];
        relative = relative.Replace("/", "\\");
        if (!relative.StartsWith("\\", StringComparison.Ordinal))
            relative = "\\" + relative;
        return relative;
    }
}
