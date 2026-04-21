using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game.Patch.V3;

namespace XIVLauncher.Common.Game.Integrity;

public static class IntegrityCheck
{
    private static readonly JsonSerializerOptions DefaultOption = new() { WriteIndented = true };

    public static async Task<string> GenerateIntegrityAsync(IProgress<IntegrityCheckProgress> progress, DirectoryInfo gamePath)
    {
        var localIntegrity = await RunIntegrityCheckAsync(gamePath, progress).ConfigureAwait(false);
        SaveToJson(localIntegrity, out var path);
        return path;
    }

    public static async Task<(IntegrityCheckCompareResult compareResult, string report, IntegrityCheckResult? remoteIntegrity)> CompareIntegrityAsync
    (
        IProgress<IntegrityCheckProgress> progress,
        DirectoryInfo                     gamePath,
        bool                              onlyIndex = false
    )
    {
        IntegrityCheckResult remoteIntegrity;

        try
        {
            remoteIntegrity = await DownloadIntegrityCheckForVersion().ConfigureAwait(false);
            var localVersion = Repository.Ffxiv.GetVer(gamePath);
            if (!string.Equals(localVersion, remoteIntegrity.GameVersion, StringComparison.Ordinal))
                return (IntegrityCheckCompareResult.ReferenceNotFound, string.Empty, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (IntegrityCheckCompareResult.ReferenceFetchFailure, string.Empty, null);
        }

        var localIntegrity = await RunIntegrityCheckAsync(gamePath, progress, onlyIndex).ConfigureAwait(false);
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

        return (failed ? IntegrityCheckCompareResult.Invalid : IntegrityCheckCompareResult.Valid, report, remoteIntegrity);
    }

    public static async Task<Dictionary<string, V3VersionMappingEntry>> GetVersionMapping(CancellationToken cancellationToken = default)
    {
        using var metadataClient = new V3GamePatchMetadataClient();
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
        using var metadataClient = new V3GamePatchMetadataClient();
        return await metadataClient.DownloadIntegrityCheck(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> DownloadLatestLocalVersionFile(CancellationToken cancellationToken = default)
    {
        using var metadataClient = new V3GamePatchMetadataClient();
        return await metadataClient.DownloadLatestLocalVersionFile(cancellationToken).ConfigureAwait(false);
    }

    public static Task<IntegrityCheckResult> RunIntegrityCheckAsync
    (
        DirectoryInfo                      gamePath,
        IProgress<IntegrityCheckProgress>? progress,
        bool                               onlyIndex = false
    )
    {
        var files = CheckDirectory(gamePath, gamePath.FullName, progress, onlyIndex);
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
        bool                               onlyIndex = false
    )
    {
        var filesToProcess = new List<FileInfo>();
        CollectFiles(directory, rootDirectory, onlyIndex, filesToProcess);

        var results = new ConcurrentDictionary<string, (string Hash, ulong Size)>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Math.Max(Environment.ProcessorCount - 2, 1), 32)
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
                    progress?.Report(new() { CurrentFile = relativePath });
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

    private static void SaveToJson(IntegrityCheckResult result, out string savedPath)
    {
        var jsonObject = new
        {
            result.Hashes,
            result.GameVersion,
            LastGameVersion = string.Empty
        };

        var json          = JsonSerializer.Serialize(jsonObject, DefaultOption);
        var fileName      = $"{result.GameVersion}.json";
        var directoryName = Path.Combine(Paths.RoamingPath, "gameHashes");
        if (!Directory.Exists(directoryName))
            Directory.CreateDirectory(directoryName);

        var filePath = Path.Combine(directoryName, fileName);
        File.WriteAllText(filePath, json);
        savedPath = filePath;
    }
}
