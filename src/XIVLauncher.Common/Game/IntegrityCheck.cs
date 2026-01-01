using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Game
{
    public static class IntegrityCheck
    {
        public const string INTEGRITY_CHECK_BASE_URL = "https://v3launcher.jijiagames.com/v3launcher/build/100001900/8847";

        public class IntegrityCheckResult
        {
            public Dictionary<string, string> Hashes { get; set; }
            public string GameVersion { get; set; }
            public string LastGameVersion { get; set; }
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
            ReferenceFetchFailure,
        }

        public record class VersionInfo(string V, string View);

        public static async Task<(CompareResult compareResult, string report, IntegrityCheckResult remoteIntegrity)>
            CompareIntegrityAsync(IProgress<IntegrityCheckProgress> progress, DirectoryInfo gamePath, bool onlyIndex = false)
        {
            IntegrityCheckResult remoteIntegrity;
            try
            {
                remoteIntegrity = await DownloadIntegrityCheckForVersion();
                var localVersion = Repository.Ffxiv.GetVer(gamePath);
                if (localVersion != remoteIntegrity.GameVersion)
                {
                    return (CompareResult.ReferenceNotFound, null, null);
                }
            }
            catch (WebException e)
            {
                return (CompareResult.ReferenceFetchFailure, null, null);
            }

            var localIntegrity = await RunIntegrityCheckAsync(gamePath, progress, onlyIndex).ConfigureAwait(false);

            var report = "";
            var failed = false;

            foreach (var hashEntry in remoteIntegrity.Hashes)
            {
                if (onlyIndex && (!hashEntry.Key.EndsWith(".index", StringComparison.Ordinal) && !hashEntry.Key.EndsWith(".index2", StringComparison.Ordinal)))
                    continue;

                if (localIntegrity.Hashes.Any(h => h.Key == hashEntry.Key))
                {
                    if (localIntegrity.Hashes.First(h => h.Key == hashEntry.Key).Value != hashEntry.Value)
                    {
                        report += $"Mismatch: {hashEntry.Key}\n";
                        failed = true;
                    }
                }
                else
                {
                    report += $"Missing: {hashEntry.Key}\n";
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
                var url = $"https://ff.autopatch.sdo.com/v3launcher/mapping/v2v3Check.json?time={timestamp}";

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

        public static async Task<IntegrityCheckResult> DownloadIntegrityCheckForVersion()
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Host", "v3launcher.jijiagames.com");
                var url = $"{INTEGRITY_CHECK_BASE_URL}/client-all-files-list/client_all_files_list.dat";
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var reponseText = await response.Content.ReadAsStringAsync();
                var integrityLines = reponseText.Trim().Split();
                var result = new IntegrityCheckResult();
                result.Hashes = new Dictionary<string, string>();
                var dataVersion = integrityLines[0].Split('|')[2];
                var versionMapping = await GetVersionMapping();
                var gameVersion = versionMapping.FirstOrDefault(x => x.Value.V == dataVersion);
                result.GameVersion = gameVersion.Key;
                for (int i = 1; i < integrityLines.Length; i++)
                {
                    var filePath = integrityLines[i].Split('|')[0];
                    if (!filePath.StartsWith("\\"))
                        filePath = "\\" + filePath;
                    var md5 = integrityLines[i].Split('|')[2];
                    result.Hashes.Add(filePath, md5);
                }
                return result;
            }
        }

        public static async Task<IntegrityCheckResult> RunIntegrityCheckAsync(DirectoryInfo gamePath,
                                                                              IProgress<IntegrityCheckProgress> progress, bool onlyIndex = false)
        {
            //var hashes = new Dictionary<string, string>();

            var hashes = CheckDirectory(gamePath, gamePath.FullName, progress, onlyIndex);

            return new IntegrityCheckResult
            {
                GameVersion = Repository.Ffxiv.GetVer(gamePath),
                Hashes = hashes.ToDictionary()
            };
        }

        private static ConcurrentDictionary<string, string> CheckDirectory(
            DirectoryInfo directory,
            string rootDirectory,
            IProgress<IntegrityCheckProgress> progress,
            bool onlyIndex = false)
        {
            var filesToProcess = new List<FileInfo>();
            CollectFiles(directory, rootDirectory, onlyIndex, filesToProcess);

            var results = new ConcurrentDictionary<string, string>();
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

            Parallel.ForEach(filesToProcess, options, file =>
            {
                try
                {
                    var relativePath = GetRelativePath(file.FullName, rootDirectory);

                    using var md5 = MD5.Create();
                    using var stream = new BufferedStream(
                        file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                        bufferSize: 1200000);

                    var hash = md5.ComputeHash(stream);
                    var hashString = BitConverter.ToString(hash).Replace("-", string.Empty);

                    results.TryAdd(relativePath, hashString);

                    // UI只有一个显示文件的位置，先不改，反正看着UI在动就行，省的以为卡住了
                    progress?.Report(new IntegrityCheckProgress
                    {
                        CurrentFile = relativePath
                    });
                }
                catch (IOException)
                {
                    // Ignore
                }
            });
            return results;
        }

        private static void CollectFiles(
            DirectoryInfo directory,
            string rootDirectory,
            bool onlyIndex,
            List<FileInfo> filesToProcess)
        {
            foreach (var file in directory.GetFiles())
            {
                var relativePath = GetRelativePath(file.FullName, rootDirectory);

                if (!relativePath.StartsWith("\\game", StringComparison.Ordinal))
                    continue;

                if (relativePath.StartsWith("\\game\\My Games", StringComparison.Ordinal))
                    continue;

                if (onlyIndex &&
                    !relativePath.EndsWith(".index", StringComparison.Ordinal) &&
                    !relativePath.EndsWith(".index2", StringComparison.Ordinal))
                    continue;

                filesToProcess.Add(file);
            }

            foreach (var dir in directory.GetDirectories())
            {
                if (!dir.FullName.ToLower().Contains("shade"))
                {
                    CollectFiles(dir, rootDirectory, onlyIndex, filesToProcess);
                }
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
    }
}
