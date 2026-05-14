using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.GamePatchV3;

public class SdoFileDownloader : IDisposable
{
    public int ProgressReportInterval { get; set; } = DEFAULT_PROGRESS_REPORT_INTERVAL;

    private readonly HttpClient client = new();
    private readonly List<string> hashes = [];
    private readonly ConcurrentDictionary<int, string> queuedDownloads = new();
    private readonly List<string> relativePaths = [];
    private readonly List<bool> brokenStates = [];
    private readonly List<ulong> sizes = [];

    private long lastProgressTimestamp;
    private string downloadBaseUrl = null!;
    private string dataVersion = null!;

    public void Dispose() =>
        client.Dispose();

    public void ConstructFromRemoteIntegrity(IntegrityCheckResult remoteIntegrity)
    {
        relativePaths.Clear();
        hashes.Clear();
        sizes.Clear();
        brokenStates.Clear();
        queuedDownloads.Clear();

        downloadBaseUrl = remoteIntegrity.BaseUrl ?? throw new ArgumentException("Remote integrity must contain a download base URL.", nameof(remoteIntegrity));
        dataVersion = remoteIntegrity.DataVersion ?? throw new ArgumentException("Remote integrity must contain a data version.", nameof(remoteIntegrity));

        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            client.DefaultRequestHeaders.UserAgent.ParseAdd(USER_AGENT);

        foreach (var (relativePath, hash) in remoteIntegrity.Hashes)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            relativePaths.Add(relativePath);
            hashes.Add(hash ?? string.Empty);
            sizes.Add(remoteIntegrity.Sizes is not null && remoteIntegrity.Sizes.TryGetValue(relativePath, out var size) ? size : 0);
            brokenStates.Add(false);
        }
    }

    public async Task VerifyFiles(string gameRootPath, bool refine = false, int concurrentCount = 8, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var candidates = new List<int>(relativePaths.Count);
        ulong totalSize = 0;
        for (var index = 0; index < relativePaths.Count; index++)
        {
            if (refine && !brokenStates[index])
                continue;

            candidates.Add(index);
            totalSize += sizes[index];
        }

        var reportMax = GetReportSize(totalSize);
        long reportedSize = 0;
        var reportedCount = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, concurrentCount),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync
        (
            candidates,
            parallelOptions,
            async (targetIndex, ct) =>
            {
                var localPath = GetLocalPath(gameRootPath, relativePaths[targetIndex]);
                Log.Information("Verifying file: {Path}", localPath);

                var isBroken = true;
                try
                {
                    if (File.Exists(localPath))
                    {
                        var fileInfo = new FileInfo(localPath);
                        if ((ulong)fileInfo.Length == sizes[targetIndex])
                        {
                            var fileHash = await GameIntegrityChecker.GetFileMd5Hash(localPath, ct);
                            isBroken = !string.Equals(fileHash, hashes[targetIndex], StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "Failed to verify file: {Path}", localPath);
                }

                brokenStates[targetIndex] = isBroken;

                var reportCurrent = Interlocked.Add(ref reportedSize, GetReportSize(sizes[targetIndex]));
                var reportCount = Interlocked.Increment(ref reportedCount);
                ReportVerifyProgress(targetIndex, reportCount, reportCurrent, reportMax);
            }
        );
    }

    public void QueueInstall(int targetIndex, string filePath)
    {
        if ((uint)targetIndex >= (uint)relativePaths.Count)
            throw new ArgumentOutOfRangeException(nameof(targetIndex));

        Log.Information("Queueing download for {RelativePath}", relativePaths[targetIndex]);
        queuedDownloads[targetIndex] = filePath;
    }

    public async Task Install(string gameRootPath, int concurrentCount, CancellationToken cancellationToken = default)
    {
        var queue = queuedDownloads.ToArray();
        if (queue.Length == 0)
            return;

        long totalContentBytes = 0;
        long totalDownloadedBytes = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, concurrentCount),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync
        (
            queue,
            parallelOptions,
            async (item, ct) =>
            {
                var targetIndex = item.Key;
                var relativePath = relativePaths[targetIndex];
                var targetFilePath = GetLocalPath(gameRootPath, relativePath);
                var targetDirPath = Path.GetDirectoryName(targetFilePath) ?? throw new InvalidOperationException("Invalid target path");
                Directory.CreateDirectory(targetDirPath);

                ReportInstallProgress(targetIndex, 0, 0, InstallTaskState.Connecting);
                var downloadUrl = GetDownloadUrl(item.Value);
                Log.Information("Downloading {RelativePath} from {DownloadUrl}", relativePath, downloadUrl);

                using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var contentLength = response.Content.Headers.ContentLength ?? 0;
                ReportInstallProgress(targetIndex, Interlocked.Read(ref totalDownloadedBytes), Interlocked.Add(ref totalContentBytes, contentLength), InstallTaskState.Downloading);

                var tempPath = string.Concat(targetFilePath, TEMP_EXTENSION);
                var complete = false;

                try
                {
                    {
                        await using var source = await response.Content.ReadAsStreamAsync(ct);
                        await using var sink = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, FILE_STREAM_BUFFER_SIZE, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        using var buffer = ReusableByteBufferManager.GetBuffer();

                        while (true)
                        {
                            var read = await source.ReadAsync(buffer.Buffer.AsMemory(0, buffer.Buffer.Length), ct);
                            if (read == 0)
                                break;

                            await sink.WriteAsync(buffer.Buffer.AsMemory(0, read), ct);
                            ReportInstallProgress(targetIndex, Interlocked.Add(ref totalDownloadedBytes, read), Interlocked.Read(ref totalContentBytes), InstallTaskState.Downloading);
                        }
                    }

                    File.Move(tempPath, targetFilePath, true);
                    complete = true;
                    brokenStates[targetIndex] = false;
                    ReportInstallProgress(targetIndex, Interlocked.Read(ref totalDownloadedBytes), Interlocked.Read(ref totalContentBytes), InstallTaskState.Complete);
                }
                finally
                {
                    if (!complete)
                    {
                        try
                        {
                            File.Delete(tempPath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            Log.Warning(ex, "Failed to delete temp file: {Path}", tempPath);
                        }
                    }
                }
            }
        );

        queuedDownloads.Clear();
    }

    public List<string> GetBrokenFiles()
    {
        var brokenFiles = new List<string>();
        for (var index = 0; index < brokenStates.Count; index++)
        {
            if (brokenStates[index])
                brokenFiles.Add(relativePaths[index]);
        }

        return brokenFiles;
    }

    private static long GetReportSize(ulong size) =>
        size > long.MaxValue ? long.MaxValue : (long)size;

    private static string GetLocalPath(string gameRootPath, string relativePath) =>
        Path.Combine(gameRootPath, relativePath.TrimStart('\\'));

    private void ReportVerifyProgress(int index, int count, long progress, long max)
    {
        if (ShouldReportProgress())
            OnVerifyProgress?.Invoke(index, count, progress, max);
    }

    private void ReportInstallProgress(int index, long progress, long max, InstallTaskState state)
    {
        if (state is not InstallTaskState.Downloading || ShouldReportProgress())
            OnInstallProgress?.Invoke(index, progress, max, state);
    }

    private bool ShouldReportProgress()
    {
        var now = Stopwatch.GetTimestamp();
        var interval = Stopwatch.Frequency * Math.Max(1, ProgressReportInterval) / 1000;
        var previous = Interlocked.Read(ref lastProgressTimestamp);

        return now - previous >= interval && Interlocked.CompareExchange(ref lastProgressTimestamp, now, previous) == previous;
    }

    private void EnsureInitialized()
    {
        if (relativePaths.Count == 0)
            throw new InvalidOperationException("Installer is not initialized.");
    }

    private string GetFileKey(string filePath)
    {
        var inputBytes = Encoding.Unicode.GetBytes($"{SdoInfos.APP_ID}_{dataVersion}_{filePath}");
        var hashBytes = MD5.HashData(inputBytes);
        return Convert.ToHexString(hashBytes);
    }

    private Uri GetDownloadUrl(string filePath)
    {
        filePath = filePath.Replace(Path.DirectorySeparatorChar, '\\').TrimStart('\\');
        var pathEnd = filePath.LastIndexOf('\\');
        var directoryPath = pathEnd < 0 ? string.Empty : filePath[..pathEnd].Replace('\\', '/');
        var uri = new Uri($"{downloadBaseUrl}/{directoryPath}/{GetFileKey(filePath)}");
        return CdnUrlSigner.Sign(uri);
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

    public event OnVerifyProgressDelegate? OnVerifyProgress;

    #region Constants

    private const int DEFAULT_PROGRESS_REPORT_INTERVAL = 250;
    private const int FILE_STREAM_BUFFER_SIZE = 131072;
    private const string TEMP_EXTENSION = ".tmp";
    private const string USER_AGENT = "FF14v3autopatch";

    #endregion
}
