using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Integrity;
using XIVLauncher.Common.Patching.Util;

namespace XIVLauncher.Common.Patching.SdoFileDownload;

public class SdoFileDownloadInstaller : IDisposable
{
    public int ProgressReportInterval { get; set; } = 250;

    private readonly HttpClient                        client          = new();
    private readonly Lock                              progressSync    = new();
    private readonly List<TargetFile>                  targets         = new();
    private readonly ConcurrentDictionary<int, string> queuedDownloads = new();

    private const string CDN_KEY = "EKUWRI5KXXAIDlQ0mBNLa7XkjU1JNFuL";
    private const string APP_ID  = "100001900";
    private       long   lastProgressTicks;
    private       string downloadBaseUrl = null!;
    private       string dataVersion     = null!;

    #region Disposal

    public void Dispose() =>
        client.Dispose();

    #endregion

    public void ConstructFromRemoteIntegrity(IntegrityCheckResult remoteIntegrity)
    {
        targets.Clear();
        queuedDownloads.Clear();
        downloadBaseUrl = remoteIntegrity.BaseUrl     ?? throw new ArgumentException("Remote integrity must contain a download base URL.", nameof(remoteIntegrity));
        dataVersion     = remoteIntegrity.DataVersion ?? throw new ArgumentException("Remote integrity must contain a data version.",      nameof(remoteIntegrity));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FF14v3autopatch");

        foreach (var (relativePath, hash) in remoteIntegrity.Hashes)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            targets.Add
            (
                new()
                {
                    RelativePath = relativePath,
                    Hash         = hash ?? string.Empty,
                    Size         = remoteIntegrity.Sizes is not null && remoteIntegrity.Sizes.TryGetValue(relativePath, out var size) ? size : 0
                }
            );
        }
    }

    public async Task VerifyFiles(string gameRootPath, bool refine = false, int concurrentCount = 8, CancellationToken cancellationToken = default)
    {
        if (!targets.Any())
            throw new InvalidOperationException("Installer is not initialized.");

        var brokenCandidates = targets
                               .Select((target, index) => new { target, index })
                               .Where(x => !refine || x.target.IsBroken)
                               .ToList();

        var  totalSize     = brokenCandidates.Aggregate(0UL, (acc, x) => acc + x.target.Size);
        var  reportMax     = totalSize > long.MaxValue ? long.MaxValue : (long)totalSize;
        long reportedSize  = 0;
        var  reportedCount = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, concurrentCount),
            CancellationToken      = cancellationToken
        };

        await Parallel.ForEachAsync
        (
            brokenCandidates,
            parallelOptions,
            async (candidate, ct) =>
            {
                var localPath = GetLocalPath(gameRootPath, candidate.target.RelativePath);
                var isBroken  = true;
                Log.Information("Verifying file: {Path}", localPath);

                try
                {
                    if (File.Exists(localPath))
                    {
                        var fileInfo = new FileInfo(localPath);

                        if ((ulong)fileInfo.Length != candidate.target.Size)
                            isBroken = true;
                        else
                        {
                            var fileHash = await IntegrityCheck.GetFileMd5Hash(localPath, ct);
                            isBroken = !string.Equals(fileHash, candidate.target.Hash, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "Failed to verify file: {Path}", localPath);
                    isBroken = true;
                }

                candidate.target.IsBroken = isBroken;

                var reportIndex     = Math.Min(candidate.index, int.MaxValue);
                var currentFileSize = candidate.target.Size > long.MaxValue ? long.MaxValue : (long)candidate.target.Size;
                var reportCurrent   = Interlocked.Add(ref reportedSize, currentFileSize);
                var reportCount     = Interlocked.Increment(ref reportedCount);
                ReportVerifyProgress(reportIndex, reportCount, reportCurrent, reportMax);
            }
        );
    }

    public void QueueInstall(int targetIndex, string filePath)
    {
        if (targetIndex < 0 || targetIndex >= targets.Count)
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        Log.Information($"Queueing download for {targets[targetIndex].RelativePath}");
        queuedDownloads[targetIndex] = filePath;
    }

    public async Task Install(string gameRootPath, int concurrentCount, CancellationToken cancellationToken = default)
    {
        var queue = queuedDownloads.ToArray();
        if (!queue.Any())
            return;
        long totalDownloadedBytes = 0;
        long totalContentBytes    = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, concurrentCount),
            CancellationToken      = cancellationToken
        };

        await Parallel.ForEachAsync
        (
            queue,
            options,
            async (item, ct) =>
            {
                var target         = targets[item.Key];
                var targetFilePath = GetLocalPath(gameRootPath, target.RelativePath);
                var targetDirPath  = Path.GetDirectoryName(targetFilePath) ?? throw new InvalidOperationException("Invalid target path");

                Directory.CreateDirectory(targetDirPath);
                ReportInstallProgress(item.Key, 0, 0, InstallTaskState.Connecting);
                var downloadUrl = GetDownloadUrl(item.Value);
                Log.Information($"Downloading {target.RelativePath} from {downloadUrl}");
                using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes               = response.Content.Headers.ContentLength ?? 0;
                var currentTotalContentBytes = Interlocked.Add(ref totalContentBytes, totalBytes);
                ReportInstallProgress(item.Key, Interlocked.Read(ref totalDownloadedBytes), currentTotalContentBytes, InstallTaskState.Downloading);

                var tempPath = targetFilePath + ".tmp";
                var complete = false;

                try
                {
                    await using (var source = await response.Content.ReadAsStreamAsync(ct))
                    await using (var sink = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var buffer = ReusableByteBufferManager.GetBuffer())
                    {
                        while (true)
                        {
                            var read = await source.ReadAsync(buffer.Buffer, 0, buffer.Buffer.Length, ct);
                            if (read <= 0)
                                break;

                            await sink.WriteAsync(buffer.Buffer, 0, read, ct);
                            var currentTotalDownloadedBytes = Interlocked.Add(ref totalDownloadedBytes, read);
                            ReportInstallProgress(item.Key, currentTotalDownloadedBytes, Interlocked.Read(ref totalContentBytes), InstallTaskState.Downloading);
                        }
                    }

                    if (File.Exists(targetFilePath))
                        File.Delete(targetFilePath);

                    File.Move(tempPath, targetFilePath);
                    complete        = true;
                    target.IsBroken = false;
                    ReportInstallProgress(item.Key, Interlocked.Read(ref totalDownloadedBytes), Interlocked.Read(ref totalContentBytes), InstallTaskState.Complete);
                }
                finally
                {
                    if (!complete && File.Exists(tempPath))
                    {
                        try
                        {
                            File.Delete(tempPath);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Failed to delete temp file: {Path}", tempPath);
                        }
                    }
                }
            }
        );

        queuedDownloads.Clear();
    }

    public List<string> GetBrokenFiles() =>
        targets
            .Where(x => x.IsBroken)
            .Select(x => x.RelativePath)
            .ToList();

    private static string GetLocalPath(string gameRootPath, string relativePath) =>
        Path.Combine(gameRootPath, relativePath.TrimStart('\\'));

    private void ReportVerifyProgress(int index, int count, long progress, long max)
    {
        if (!ShouldReportProgress())
            return;

        OnVerifyProgress?.Invoke(index, count, progress, max);
    }

    private void ReportInstallProgress(int index, long progress, long max, InstallTaskState state)
    {
        if (state is InstallTaskState.Downloading && !ShouldReportProgress())
            return;

        OnInstallProgress?.Invoke(index, progress, max, state);
    }

    private bool ShouldReportProgress()
    {
        var nowTicks = DateTime.UtcNow.Ticks;

        lock (progressSync)
        {
            var minIntervalTicks = TimeSpan.FromMilliseconds(Math.Max(1, ProgressReportInterval)).Ticks;
            if (nowTicks - lastProgressTicks < minIntervalTicks)
                return false;

            lastProgressTicks = nowTicks;
            return true;
        }
    }

    private string GetFileKey(string filePath)
    {
        using (var md5 = MD5.Create())
        {
            var inputBytes = Encoding.Unicode.GetBytes($"{APP_ID}_{dataVersion}_{filePath}");
            var hashBytes  = md5.ComputeHash(inputBytes);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToUpper();
        }
    }

    private Uri GetDownloadUrl(string filePath)
    {
        filePath = filePath.Replace(Path.DirectorySeparatorChar, '\\').TrimStart('\\');
        var pathParts    = filePath.Split('\\');
        var path         = string.Join("/", pathParts.Take(pathParts.Length - 1));
        var uri          = new Uri($"{downloadBaseUrl}/{path}/{GetFileKey(filePath)}");
        var uriPath      = uri.AbsolutePath;
        var timeStamp    = DateTimeOffset.Now.ToUnixTimeSeconds();
        var timeStampHex = timeStamp.ToString("x").ToLower();

        using (var md5 = MD5.Create())
        {
            var inputBytes = Encoding.UTF8.GetBytes($"{CDN_KEY}{uriPath}{timeStampHex}");
            var hashBytes  = md5.ComputeHash(inputBytes);
            var cdnKey     = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
            return new Uri($"{uri.Scheme}://{uri.Host}/{cdnKey}/{timeStampHex}{uriPath}");
        }
    }

    public enum InstallTaskState
    {
        NotStarted,
        Connecting,
        Downloading,
        Complete
    }

    public delegate void OnInstallProgressDelegate(int index, long progress, long max, InstallTaskState state);

    public delegate void OnVerifyProgressDelegate(int index, int count, long progress, long max);

    public event OnInstallProgressDelegate? OnInstallProgress;
    public event OnVerifyProgressDelegate?  OnVerifyProgress;

    private class TargetFile
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Hash         { get; set; } = string.Empty;
        public ulong  Size         { get; set; }
        public bool   IsBroken     { get; set; }
    }
}
