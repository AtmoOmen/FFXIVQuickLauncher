using System.Security.Cryptography;
using System.Text;
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
            var minimumSupportedDataVersion = targetArea == null ?
                                                  SdoInfos.DEFAULT_MINIMUM_SUPPORTED_DATA_VERSION :
                                                  GamePatchMetadataClient.ResolveMinimumSupportedDataVersion(targetArea);
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

        var localIntegrity = await RunIntegrityCheckAsync(gamePath, progress, onlyIndex, cancellationToken).ConfigureAwait(false);
        return CompareIntegrity(remoteIntegrity, localIntegrity, onlyIndex);
    }

    internal static IntegrityCheckCompareOutcome CompareIntegrity
    (
        IntegrityCheckResult remoteIntegrity,
        IntegrityCheckResult localIntegrity,
        bool                 onlyIndex = false
    )
    {
        var remoteIntegrityEntries = IntegrityPathEntry.BuildEntries(remoteIntegrity);
        var report                 = new StringBuilder();
        var failed                 = false;

        foreach (var hashEntry in remoteIntegrityEntries)
        {
            if (onlyIndex                                                                           &&
                !hashEntry.CanonicalSdoPath.EndsWith(".index",  StringComparison.OrdinalIgnoreCase) &&
                !hashEntry.CanonicalSdoPath.EndsWith(".index2", StringComparison.OrdinalIgnoreCase))
                continue;

            if (hashEntry.CanonicalSdoPath.Equals("\\game\\LocalVersion3.xml", StringComparison.OrdinalIgnoreCase))
                continue;

            if (localIntegrity.Hashes.TryGetValue(hashEntry.CanonicalSdoPath, out var localHash))
            {
                if (localIntegrity.Sizes.TryGetValue(hashEntry.CanonicalSdoPath, out var localSize) && localSize != hashEntry.Size)
                {
                    report.Append("Size mismatch: ").AppendLine(hashEntry.CanonicalSdoPath);
                    failed = true;
                    continue;
                }

                if (!string.Equals(localHash, hashEntry.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    report.Append("Mismatch: ").AppendLine(hashEntry.CanonicalSdoPath);
                    failed = true;
                }
            }
            else
            {
                report.Append("Missing: ").AppendLine(hashEntry.CanonicalSdoPath);
                failed = true;
            }
        }

        return new IntegrityCheckCompareOutcome
        {
            CompareResult = failed ?
                                IntegrityCheckCompareResult.Invalid :
                                IntegrityCheckCompareResult.Valid,
            Report          = report.ToString(),
            RemoteIntegrity = remoteIntegrity
        };
    }

    public static async Task<string> GetFileMd5Hash
    (
        string            filePath,
        CancellationToken cancellationToken = default
    )
    {
        await using var stream = new FileStream
        (
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            HASH_BUFFER_SIZE,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        var hash = await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public static async Task<IntegrityCheckResult> DownloadIntegrityCheckForVersion
    (
        CancellationToken cancellationToken = default
    )
    {
        using var metadataClient = new GamePatchMetadataClient();
        return await metadataClient.DownloadIntegrityCheck(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IntegrityCheckResult> RunIntegrityCheckAsync
    (
        DirectoryInfo                      gamePath,
        IProgress<IntegrityCheckProgress>? progress,
        bool                               onlyIndex         = false,
        CancellationToken                  cancellationToken = default
    )
    {
        var files = await CheckDirectoryAsync(gamePath, progress, onlyIndex, cancellationToken).ConfigureAwait(false);
        return new IntegrityCheckResult
        {
            GameVersion = Repository.Ffxiv.GetVer(gamePath),
            Hashes      = files.ToDictionary(x => x.Path, x => x.Hash, StringComparer.OrdinalIgnoreCase),
            Sizes       = files.ToDictionary(x => x.Path, x => x.Size, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static async Task<List<(string Path, string Hash, ulong Size)>> CheckDirectoryAsync
    (
        DirectoryInfo                      directory,
        IProgress<IntegrityCheckProgress>? progress,
        bool                               onlyIndex,
        CancellationToken                  cancellationToken
    )
    {
        var rootDirectory = directory.FullName;
        var filesToProcess = await Task.Run
                             (
                                 () =>
                                 {
                                     var files = new List<FileInfo>();
                                     CollectFiles(directory, rootDirectory, onlyIndex, files, cancellationToken);
                                     return files;
                                 },
                                 cancellationToken
                             ).ConfigureAwait(false);

        var results            = new (string Path, string Hash, ulong Size)?[filesToProcess.Count];
        var processedFileCount = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Math.Max(Environment.ProcessorCount, 1), MAX_HASH_CONCURRENCY),
            CancellationToken      = cancellationToken
        };

        await Parallel.ForAsync
        (
            0,
            filesToProcess.Count,
            options,
            async (fileIndex, token) =>
            {
                var file = filesToProcess[fileIndex];

                try
                {
                    var relativePath = GetRelativePath(file.FullName, rootDirectory);
                    await using var stream = new FileStream
                    (
                        file.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        HASH_BUFFER_SIZE,
                        FileOptions.Asynchronous | FileOptions.SequentialScan
                    );
                    var size       = (ulong)stream.Length;
                    var hash       = await MD5.HashDataAsync(stream, token).ConfigureAwait(false);
                    var hashString = Convert.ToHexString(hash);

                    results[fileIndex] = (relativePath, hashString, size);
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "[IntegrityCheck] 无法读取游戏文件 {Path}", file.FullName);
                }
            }
        ).ConfigureAwait(false);

        return results.OfType<(string Path, string Hash, ulong Size)>().ToList();
    }

    private static void CollectFiles
    (
        DirectoryInfo     directory,
        string            rootDirectory,
        bool              onlyIndex,
        List<FileInfo>    filesToProcess,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var file in directory.GetFiles())
        {
            var relativePath = GetRelativePath(file.FullName, rootDirectory);

            if (!GamePathNormalizer.TryNormalizeGameRelativePath(relativePath, out var normalizedGameRelativePath))
                continue;

            if (normalizedGameRelativePath.StartsWith("game/My Games/", StringComparison.OrdinalIgnoreCase))
                continue;

            if (normalizedGameRelativePath.Equals("game/LocalVersion3.xml", StringComparison.OrdinalIgnoreCase))
                continue;

            if (onlyIndex                                                             &&
                !relativePath.EndsWith(".index",  StringComparison.OrdinalIgnoreCase) &&
                !relativePath.EndsWith(".index2", StringComparison.OrdinalIgnoreCase))
                continue;

            filesToProcess.Add(file);
        }

        foreach (var dir in directory.GetDirectories())
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) == 0 && !dir.Name.Contains("shade", StringComparison.OrdinalIgnoreCase))
                CollectFiles(dir, rootDirectory, onlyIndex, filesToProcess, cancellationToken);
        }
    }

    private static string GetRelativePath
    (
        string fullPath,
        string rootDirectory
    )
    {
        var relative = Path.GetRelativePath(rootDirectory, fullPath).Replace('/', '\\');
        return "\\" + relative.TrimStart('\\');
    }

    private const int HASH_BUFFER_SIZE     = 131072;
    private const int MAX_HASH_CONCURRENCY = 4;
}
