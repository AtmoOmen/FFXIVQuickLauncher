using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace XIVLauncher.Common.Game;

public static class IntegrityCheck
{
    public const string INTEGRITY_CHECK_BASE_URL = "https://gh.atmoomen.top/https://raw.githubusercontent.com/Dalamud-DailyRoutines/XLCNSoilAssets/master/integrity/";
    
    private static readonly JsonSerializerOptions DefaultOption = new() { WriteIndented = true };

    public class IntegrityCheckResult
    {
        public Dictionary<string, string> Hashes          { get; set; }
        public string                     GameVersion     { get; set; }
        public string                     LastGameVersion { get; set; }
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

    private static readonly HttpClient HttpClient = new();

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
            remoteIntegrity = DownloadIntegrityCheckForVersion(Repository.Ffxiv.GetVer(gamePath));
        }
        catch (HttpRequestException e)
        {
            if (e.StatusCode == HttpStatusCode.NotFound)
                return (CompareResult.ReferenceNotFound, null, null);
            return (CompareResult.ReferenceFetchFailure, null, null);
        }

        var localIntegrity = await RunIntegrityCheckAsync(gamePath, progress, onlyIndex).ConfigureAwait(false);

        var report = string.Empty;
        var failed = false;

        foreach (var hashEntry in remoteIntegrity.Hashes)
        {
            if (onlyIndex && !hashEntry.Key.EndsWith(".index", StringComparison.Ordinal) && !hashEntry.Key.EndsWith(".index2", StringComparison.Ordinal))
                continue;

            if (localIntegrity.Hashes.Any(h => h.Key == hashEntry.Key))
            {
                if (localIntegrity.Hashes.First(h => h.Key == hashEntry.Key).Value != hashEntry.Value)
                {
                    report += $"Mismatch: {hashEntry.Key}\n";
                    failed =  true;
                }
            }
            else
                report += $"Missing: {hashEntry.Key}\n";
        }

        return (failed ? CompareResult.Invalid : CompareResult.Valid, report, remoteIntegrity);
    }

    public static IntegrityCheckResult DownloadIntegrityCheckForVersion(string gameVersion)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, INTEGRITY_CHECK_BASE_URL + gameVersion + ".json");
        request.Headers.Add("X-Machine-Token", SdoUtils.GetDeviceID());

        using var response = HttpClient.Send(request);
        response.EnsureSuccessStatusCode();

        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonSerializer.Deserialize<IntegrityCheckResult>(json);
    }

#pragma warning disable CS1998
    public static async Task<IntegrityCheckResult> RunIntegrityCheckAsync
#pragma warning restore CS1998
    (
        DirectoryInfo                     gamePath,
        IProgress<IntegrityCheckProgress> progress,
        bool                              onlyIndex = false
    )
    {
        var hashes = new Dictionary<string, string>();

        using (var sha1 = SHA1.Create())
            CheckDirectory(gamePath, sha1, gamePath.FullName, ref hashes, progress, onlyIndex);

        return new IntegrityCheckResult
        {
            GameVersion = Repository.Ffxiv.GetVer(gamePath),
            Hashes      = hashes
        };
    }

    private static void CheckDirectory
    (
        DirectoryInfo                     directory,
        SHA1                              sha1,
        string                            rootDirectory,
        ref Dictionary<string, string>    results,
        IProgress<IntegrityCheckProgress> progress,
        bool                              onlyIndex = false
    )
    {
        foreach (var file in directory.GetFiles())
        {
            var relativePath = file.FullName[rootDirectory.Length..];

            // for unix compatibility with windows-generated integrity files.
            relativePath = relativePath.Replace("/", "\\");

            if (!relativePath.StartsWith('\\'))
                relativePath = "\\" + relativePath;

            if (!relativePath.StartsWith(@"\game", StringComparison.Ordinal))
                continue;

            if (relativePath.StartsWith(@"\game\My Games", StringComparison.Ordinal))
                continue;

            if (onlyIndex && !relativePath.EndsWith(".index", StringComparison.Ordinal) && !relativePath.EndsWith(".index2", StringComparison.Ordinal))
                continue;

            try
            {
                using var stream =
                    new BufferedStream(file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite), 1200000);

                var hash = sha1.ComputeHash(stream);

                results.Add(relativePath, BitConverter.ToString(hash).Replace('-', ' '));

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

        foreach (var dir in directory.GetDirectories())
        {
            // skip gshade directories. They just waste cpu
            if (!dir.FullName.Contains("shade", StringComparison.CurrentCultureIgnoreCase))
                CheckDirectory(dir, sha1, rootDirectory, ref results, progress, onlyIndex);
        }
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
}
