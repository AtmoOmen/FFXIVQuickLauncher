using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.Common.Game;

public static class IntegrityCheck
{
    public const string INTEGRITY_CHECK_BASE_URL = "https://v3launcher.jijiagames.com/v3launcher/build/100001900/8847";

    private static readonly JsonSerializerOptions DefaultOption = new() { WriteIndented = true };

    public static async Task<string> GenerateIntegrityAsync(IProgress<IntegrityCheckProgress> progress, DirectoryInfo gamePath)
    {
        var localIntegrity = await RunIntegrityCheckAsync(gamePath, progress).ConfigureAwait(false);
        SaveToJson(localIntegrity, out var path);

        return path;
    }

    public static async Task<(CompareResult compareResult, string report, IntegrityCheckResult remoteIntegrity)>
        CompareIntegrityAsync(IProgress<IntegrityCheckProgress> progress, DirectoryInfo gamePath, bool onlyIndex = false)
    {
        IntegrityCheckResult remoteIntegrity;

        try
        {
            remoteIntegrity = await DownloadIntegrityCheckForVersion();
            var localVersion = Repository.Ffxiv.GetVer(gamePath);
            if (localVersion != remoteIntegrity.GameVersion)
                return (CompareResult.ReferenceNotFound, null, null);
        }
        catch (WebException)
        {
            return (CompareResult.ReferenceFetchFailure, null, null);
        }

        var localIntegrity = await RunIntegrityCheckAsync(gamePath, progress, onlyIndex).ConfigureAwait(false);

        var report = "";
        var failed = false;

        foreach (var hashEntry in remoteIntegrity.Hashes
                                                 .Where
                                                 (hashEntry => !onlyIndex
                                                               || hashEntry.Key.EndsWith(".index",  StringComparison.Ordinal)
                                                               || hashEntry.Key.EndsWith(".index2", StringComparison.Ordinal)
                                                 ))
        {
            if (localIntegrity.Hashes.TryGetValue(hashEntry.Key, out var localHash))
            {
                if (remoteIntegrity.Sizes is not null
                    && localIntegrity.Sizes is not null
                    && remoteIntegrity.Sizes.TryGetValue(hashEntry.Key, out var remoteSize)
                    && localIntegrity.Sizes.TryGetValue(hashEntry.Key, out var localSize)
                    && localSize != remoteSize)
                {
                    report += $"Size mismatch: {hashEntry.Key}\n";
                    failed =  true;
                    continue;
                }

                if (localHash != hashEntry.Value)
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

        return (failed ? CompareResult.Invalid : CompareResult.Valid, report, remoteIntegrity);
    }

    public static async Task<Dictionary<string, VersionInfo>> GetVersionMapping()
    {
        using (var client = new HttpClient())
        {
            // Use the URL provided in the context to fetch version mapping
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var url       = $"https://ff.autopatch.sdo.com/v3launcher/mapping/v2v3Check.json?time={timestamp}";

            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var responseText = await response.Content.ReadAsStringAsync();

            // Parse the JSON response into a dictionary
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var versionMapping = JsonSerializer.Deserialize<Dictionary<string, VersionInfo>>(responseText, options);

            return versionMapping;
        }
    }

    public static async Task<string> GetFileMd5Hash(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       md5    = MD5.Create();
        var             hash   = await md5.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    public static async Task<IntegrityCheckResult> DownloadIntegrityCheckForVersion()
    {
        const string URL = $"{INTEGRITY_CHECK_BASE_URL}/client-all-files-list/client_all_files_list.dat";

        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("Host", "v3launcher.jijiagames.com");
            var response = await client.GetAsync(URL);
            response.EnsureSuccessStatusCode();
            var reponseText    = await response.Content.ReadAsStringAsync();
            var integrityLines = reponseText.Trim().Split();
            var result = new IntegrityCheckResult
            {
                Hashes = new Dictionary<string, string>(),
                Sizes  = new Dictionary<string, ulong>()
            };
            var lineParts   = integrityLines[0].Split('|');
            var dataVersion = lineParts[2];
            result.DataVersion = dataVersion;
            result.AppId       = lineParts[1];
            result.BaseUrl     = lineParts[0];
            var versionMapping = await GetVersionMapping();
            var gameVersion    = versionMapping.FirstOrDefault(x => x.Value.V == dataVersion);
            result.GameVersion = gameVersion.Key;

            for (var i = 1; i < integrityLines.Length; i++)
            {
                lineParts = integrityLines[i].Split('|');
                var filePath = lineParts[0];
                if (!filePath.StartsWith('\\'))
                    filePath = "\\" + filePath;
                if (!filePath.StartsWith(@"\game\", StringComparison.Ordinal))
                    continue;
                var size = ulong.Parse(lineParts[1]);
                var md5  = lineParts[2];
                result.Hashes.Add(filePath, md5);
                result.Sizes.Add(filePath, size);
            }

            return result;
        }
    }

    public static async Task<IntegrityCheckResult> RunIntegrityCheckAsync
    (
        DirectoryInfo                      gamePath,
        IProgress<IntegrityCheckProgress>? progress,
        bool                               onlyIndex = false
    )
    {
        //var hashes = new Dictionary<string, string>();

        var files = CheckDirectory(gamePath, gamePath.FullName, progress, onlyIndex);

        return new IntegrityCheckResult
        {
            GameVersion = Repository.Ffxiv.GetVer(gamePath),
            Hashes      = files.ToDictionary(x => x.Key, x => x.Value.Hash),
            Sizes       = files.ToDictionary(x => x.Key, x => x.Value.Size)
        };
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

                    // UI只有一个显示文件的位置，先不改，反正看着UI在动就行，省的以为卡住了
                    progress?.Report
                    (
                        new IntegrityCheckProgress
                        {
                            CurrentFile = relativePath
                        }
                    );
                }
                catch (IOException)
                {
                    // Ignore
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
            if (!dir.FullName.ToLower().Contains("shade"))
                CollectFiles(dir, rootDirectory, onlyIndex, filesToProcess);
        }
    }

    private static string GetRelativePath(string fullPath, string rootDirectory)
    {
        var relative = fullPath.Substring(rootDirectory.Length);
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

        var json     = JsonSerializer.Serialize(jsonObject, DefaultOption);
        var fileName = $"{result.GameVersion}.json";

        var directoryName = Path.Combine(Paths.RoamingPath, "gameHashes");
        if (!Directory.Exists(directoryName))
            Directory.CreateDirectory(directoryName);

        var filePath = Path.Combine(directoryName, fileName);
        File.WriteAllText(filePath, json);

        savedPath = filePath;
    }

    public class IntegrityCheckResult
    {
        public Dictionary<string, string> Hashes          { get; set; }
        public Dictionary<string, ulong>  Sizes           { get; set; }
        public string                     GameVersion     { get; set; }
        public string                     LastGameVersion { get; set; }
        public string                     BaseUrl         { get; set; }
        public string                     DataVersion     { get; set; }
        public string                     AppId           { get; set; }
    }

    public class IntegrityCheckProgress
    {
        public string CurrentFile { get; set; }
    }

    public enum CompareResult
    {
        Valid,
        Invalid,
        ReferenceNotFound,
        ReferenceFetchFailure
    }

    public record VersionInfo
    (
        string V,
        string View
    );
}
