using System.Collections.Concurrent;
using System.Security.Cryptography;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.GamePatchV3.Integrity.Models;

namespace XIVLauncher.GamePatchV3.Integrity;

public static class GameIntegrityChecker
{
    public static async Task<IntegrityCheckCompareOutcome> CompareIntegrityAsync
    (
        IProgress<IntegrityCheckProgress>? progress,
        DirectoryInfo                      gamePath,
        bool                               onlyIndex         = false,
        CancellationToken                  cancellationToken = default
    )
    {
        IntegrityCheckResult remoteIntegrity;
        var                  localVersion = Repository.Ffxiv.GetVer(gamePath).Trim().Trim('\uFEFF').Trim();

        try
        {
            using var metadataClient = new GamePatchMetadataClient();
            var       remoteVersion  = await metadataClient.DownloadRemoteVersion(cancellationToken).ConfigureAwait(false);
            var       targetArea     = remoteVersion.Areas.FirstOrDefault(area => area.Id == "0") ?? remoteVersion.Areas.FirstOrDefault();
            var minimumSupportedDataVersion = targetArea == null
                                                  ? SdoInfos.DEFAULT_MINIMUM_SUPPORTED_DATA_VERSION
                                                  : GamePatchMetadataClient.ResolveMinimumSupportedDataVersion(targetArea);
            var localResolution = GamePatchMetadataClient.ResolveLocalVersion(localVersion, remoteVersion);

            if (!GamePatchMetadataClient.IsSupportedDataVersion(localResolution.DataVersion, minimumSupportedDataVersion))
            {
                Log.Information
                (
                    "[IntegrityCheck] 当前版本过旧或无法识别, 本地 {LocalVersion}, 数据版本 {DataVersion}, 最低支持 {MinimumSupportedDataVersion}",
                    localVersion,
                    localResolution.DataVersion,
                    minimumSupportedDataVersion
                );
                return new IntegrityCheckCompareOutcome { CompareResult = IntegrityCheckCompareResult.VersionUnsupported };
            }

            remoteIntegrity = await metadataClient.DownloadIntegrityCheck(remoteVersion, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(localResolution.DataVersion, remoteIntegrity.DataVersion, StringComparison.Ordinal))
            {
                Log.Information
                (
                    "[IntegrityCheck] 当前版本没有对应的完整性参考, 本地 {LocalVersion}/{LocalDataVersion}, 远端 {RemoteVersion}/{RemoteDataVersion}",
                    localVersion,
                    localResolution.DataVersion,
                    remoteIntegrity.GameVersion,
                    remoteIntegrity.DataVersion
                );
                return new IntegrityCheckCompareOutcome { CompareResult = IntegrityCheckCompareResult.ReferenceNotFound };
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new IntegrityCheckCompareOutcome { CompareResult = IntegrityCheckCompareResult.ReferenceFetchFailure };
        }

        var localIntegrity         = await RunIntegrityCheckAsync(gamePath, progress, onlyIndex, cancellationToken).ConfigureAwait(false);
        var remoteIntegrityEntries = IntegrityPathEntry.BuildEntries(remoteIntegrity);
        var report                 = string.Empty;
        var failed                 = false;

        foreach (var hashEntry in remoteIntegrityEntries
                                  .Select(entry => new { entry.CanonicalSdoPath, entry.Hash, entry.Size })
                                  .Where
                                  (hashEntry => !onlyIndex
                                                || hashEntry.CanonicalSdoPath.EndsWith(".index",  StringComparison.Ordinal)
                                                || hashEntry.CanonicalSdoPath.EndsWith(".index2", StringComparison.Ordinal)
                                  ))
        {
            if (localIntegrity.Hashes.TryGetValue(hashEntry.CanonicalSdoPath, out var localHash))
            {
                if (localIntegrity.Sizes.TryGetValue(hashEntry.CanonicalSdoPath, out var localSize)
                    && localSize != hashEntry.Size)
                {
                    report += $"Size mismatch: {hashEntry.CanonicalSdoPath}\n";
                    failed =  true;
                    continue;
                }

                if (!string.Equals(localHash, hashEntry.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    report += $"Mismatch: {hashEntry.CanonicalSdoPath}\n";
                    failed =  true;
                }
            }
            else
            {
                report += $"Missing: {hashEntry.CanonicalSdoPath}\n";
                failed =  true;
            }
        }

        return new IntegrityCheckCompareOutcome
        {
            CompareResult   = failed ? IntegrityCheckCompareResult.Invalid : IntegrityCheckCompareResult.Valid,
            Report          = report,
            RemoteIntegrity = remoteIntegrity
        };
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
        bool                               onlyIndex         = false,
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

        var results            = new ConcurrentDictionary<string, (string Hash, ulong Size)>();
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

            if (!GamePathNormalizer.TryNormalizeGameRelativePath(relativePath, out var normalizedGameRelativePath))
                continue;

            if (normalizedGameRelativePath.StartsWith("game/My Games/", StringComparison.OrdinalIgnoreCase))
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
