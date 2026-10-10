using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;
using XIVLauncher.GamePatchV3.Integrity;
using XIVLauncher.GamePatchV3.Integrity.Models;

namespace XIVLauncher.GamePatchV3;

public class GameFileDownloader : IDisposable
{
    public int ProgressReportInterval { get; set; } = DEFAULT_PROGRESS_REPORT_INTERVAL;

    private readonly HttpClient                        client;
    private readonly List<string>                      hashes          = [];
    private readonly ConcurrentDictionary<int, string> queuedDownloads = new();
    private readonly List<string>                      relativePaths   = [];
    private readonly List<bool>                        brokenStates    = [];
    private readonly List<ulong>                       sizes           = [];

    private long   lastProgressTimestamp;
    private string downloadBaseUrl = null!;
    private string dataVersion     = null!;

    public GameFileDownloader()
        : this(new SocketsHttpHandler
               {
                   UseProxy = true,
                   Proxy    = XLProxyProvider.Current
               })
    {
    }

    internal GameFileDownloader
    (
        HttpMessageHandler handler
    ) =>
        client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionOrLower
        };

    public void Dispose() =>
        client.Dispose();

    public void Construct
    (
        IEnumerable<IntegrityPathEntry> targets,
        string                          baseUrl,
        string                          version
    )
    {
        relativePaths.Clear();
        hashes.Clear();
        sizes.Clear();
        brokenStates.Clear();
        queuedDownloads.Clear();
        Interlocked.Exchange(ref lastProgressTimestamp, 0);

        downloadBaseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        dataVersion     = version ?? throw new ArgumentNullException(nameof(version));

        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            client.DefaultRequestHeaders.UserAgent.ParseAdd(USER_AGENT);

        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.CanonicalSdoPath))
                continue;

            relativePaths.Add(target.CanonicalSdoPath);
            hashes.Add(target.Hash ?? string.Empty);
            sizes.Add(target.Size);
            brokenStates.Add(false);
        }
    }

    public async Task VerifyFiles
    (
        string            gameRootPath,
        bool              refine            = false,
        int               concurrentCount   = 8,
        CancellationToken cancellationToken = default
    )
    {
        EnsureInitialized();

        var   candidates = new List<int>(relativePaths.Count);
        ulong totalSize  = 0;

        for (var index = 0; index < relativePaths.Count; index++)
        {
            if (refine && !brokenStates[index])
                continue;

            candidates.Add(index);
            totalSize += sizes[index];
        }

        var  reportMax     = GetReportSize(totalSize);
        long reportedSize  = 0;
        var  reportedCount = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, concurrentCount),
            CancellationToken      = cancellationToken
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
                var reportCount   = Interlocked.Increment(ref reportedCount);
                ReportVerifyProgress(targetIndex, reportCount, reportCurrent, reportMax);
            }
        );
    }

    public void QueueInstall
    (
        int    targetIndex,
        string filePath
    )
    {
        if ((uint)targetIndex >= (uint)relativePaths.Count)
            throw new ArgumentOutOfRangeException(nameof(targetIndex));

        Log.Information("Queueing download for {RelativePath}", relativePaths[targetIndex]);
        queuedDownloads[targetIndex] = filePath;
    }

    public async Task Install
    (
        string            gameRootPath,
        int               concurrentCount,
        CancellationToken cancellationToken = default
    )
    {
        var queue = queuedDownloads.ToArray();
        if (queue.Length == 0)
            return;

        var progressState = new DownloadProgressState(GetTotalQueuedSize(queue));
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, concurrentCount),
            CancellationToken      = cancellationToken
        };

        await Parallel.ForEachAsync
        (
            queue,
            parallelOptions,
            (item, ct) => InstallFileAsync(item.Key, item.Value, gameRootPath, progressState, ct)
        );

        queuedDownloads.Clear();
    }

    private async ValueTask InstallFileAsync
    (
        int                   targetIndex,
        string                downloadPath,
        string                gameRootPath,
        DownloadProgressState progressState,
        CancellationToken     cancellationToken
    )
    {
        var relativePath   = relativePaths[targetIndex];
        var targetFilePath = GetLocalPath(gameRootPath, relativePath);
        var targetDirPath  = Path.GetDirectoryName(targetFilePath) ?? throw new InvalidOperationException("Invalid target path");
        var tempPath       = string.Concat(targetFilePath, TEMP_EXTENSION);
        var expectedSize   = GetReportSize(sizes[targetIndex]);
        Directory.CreateDirectory(targetDirPath);

        for (var attempt = 0; attempt < DOWNLOAD_ATTEMPT_COUNT; attempt++)
        {
            var complete            = false;
            var fileDownloadedBytes = 0L;

            try
            {
                ReportInstallProgress
                    (targetIndex, 0, expectedSize, Interlocked.Read(ref progressState.Downloaded), progressState.Total, InstallTaskState.Connecting);
                var downloadUrl = GetDownloadUrl(downloadPath);
                Log.Information
                (
                    "Downloading {RelativePath} from {DownloadUrl}, attempt {Attempt}/{AttemptCount}",
                    relativePath,
                    downloadUrl,
                    attempt + 1,
                    DOWNLOAD_ATTEMPT_COUNT
                );

                using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var contentLength = response.Content.Headers.ContentLength;
                if (expectedSize > 0 && contentLength is > 0 && contentLength.Value != expectedSize)
                    throw new InvalidDataException($"下载文件大小不符: {relativePath}, 期望 {expectedSize}, 实际 {contentLength.Value}");

                ReportInstallProgress
                    (targetIndex, 0, expectedSize, Interlocked.Read(ref progressState.Downloaded), progressState.Total, InstallTaskState.Downloading);
                var buffer = ArrayPool<byte>.Shared.Rent(FILE_STREAM_BUFFER_SIZE);

                try
                {
                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var sink = new FileStream
                    (
                        tempPath,
                        new FileStreamOptions
                        {
                            Mode              = FileMode.Create,
                            Access            = FileAccess.Write,
                            Share             = FileShare.None,
                            BufferSize        = FILE_STREAM_BUFFER_SIZE,
                            Options           = FileOptions.Asynchronous | FileOptions.SequentialScan,
                            PreallocationSize = expectedSize
                        }
                    );
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, FILE_STREAM_BUFFER_SIZE), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            break;

                        await sink.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer.AsSpan(0, read));
                        fileDownloadedBytes += read;
                        var totalDownloaded = Interlocked.Add(ref progressState.Downloaded, read);
                        ReportInstallProgress(targetIndex, fileDownloadedBytes, expectedSize, totalDownloaded, progressState.Total, InstallTaskState.Downloading);
                    }

                    await sink.FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (expectedSize > 0 && fileDownloadedBytes != expectedSize)
                        throw new InvalidDataException($"下载文件大小不符: {relativePath}, 期望 {expectedSize}, 实际 {fileDownloadedBytes}");

                    var actualHash = Convert.ToHexString(hash.GetHashAndReset());
                    if (!string.IsNullOrWhiteSpace(hashes[targetIndex]) && !string.Equals(actualHash, hashes[targetIndex], StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"下载文件校验失败: {relativePath}");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                File.Move(tempPath, targetFilePath, true);
                complete                  = true;
                brokenStates[targetIndex] = false;
                ReportInstallProgress
                    (targetIndex, fileDownloadedBytes, expectedSize, Interlocked.Read(ref progressState.Downloaded), progressState.Total, InstallTaskState.Complete);
                return;
            }
            catch (Exception ex) when
                (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                if (fileDownloadedBytes != 0)
                    Interlocked.Add(ref progressState.Downloaded, -fileDownloadedBytes);

                if (attempt == DOWNLOAD_ATTEMPT_COUNT - 1)
                    throw;

                Log.Warning(ex, "下载文件失败, 即将重试 {RelativePath}, attempt {Attempt}/{AttemptCount}", relativePath, attempt + 1, DOWNLOAD_ATTEMPT_COUNT);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
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
    }

    public List<string> GetBrokenFiles()
    {
        var brokenFiles = new List<string>();

        for (var index = 0; index < brokenStates.Count; index++)
            if (brokenStates[index])
                brokenFiles.Add(relativePaths[index]);

        return brokenFiles;
    }

    private static long GetReportSize
    (
        ulong size
    ) =>
        size > long.MaxValue ?
            long.MaxValue :
            (long)size;

    private long GetTotalQueuedSize
    (
        KeyValuePair<int, string>[] queue
    )
    {
        long total = 0;

        foreach (var item in queue)
        {
            var size = GetReportSize(sizes[item.Key]);
            if (long.MaxValue - total < size)
                return long.MaxValue;

            total += size;
        }

        return total;
    }

    private static string GetLocalPath
    (
        string gameRootPath,
        string relativePath
    )
    {
        if (!GamePathNormalizer.TryNormalizeGameRelativePath(relativePath, out var gameRelativePath))
            throw new InvalidOperationException($"Invalid game path: {relativePath}");

        return GamePathNormalizer.CombineWithRootPath(gameRootPath, gameRelativePath);
    }

    private void ReportVerifyProgress
    (
        int  index,
        int  count,
        long progress,
        long max
    )
    {
        if (ShouldReportProgress())
            OnVerifyProgress?.Invoke(index, count, progress, max);
    }

    private void ReportInstallProgress
    (
        int              index,
        long             fileProgress,
        long             fileTotal,
        long             totalProgress,
        long             total,
        InstallTaskState state
    )
    {
        if (state is not InstallTaskState.Downloading || ShouldReportProgress())
            OnInstallProgress?.Invoke(index, fileProgress, fileTotal, totalProgress, total, state);
    }

    private bool ShouldReportProgress()
    {
        var now      = Stopwatch.GetTimestamp();
        var interval = Stopwatch.Frequency * Math.Max(1, ProgressReportInterval) / 1000;
        var previous = Interlocked.Read(ref lastProgressTimestamp);

        return now - previous >= interval && Interlocked.CompareExchange(ref lastProgressTimestamp, now, previous) == previous;
    }

    private void EnsureInitialized()
    {
        if (relativePaths.Count == 0)
            throw new InvalidOperationException("Installer is not initialized.");
    }

    private string GetFileKey
    (
        string filePath
    ) =>
        GetFileKey(SdoInfos.APP_ID, dataVersion, filePath);

    internal static string GetFileKey
    (
        string appId,
        string version,
        string filePath
    )
    {
        var inputBytes = Encoding.Unicode.GetBytes($"{appId}_{version}_{filePath}");
        var hashBytes  = MD5.HashData(inputBytes);
        return Convert.ToHexString(hashBytes);
    }

    private Uri GetDownloadUrl
    (
        string filePath
    )
    {
        filePath = GamePathNormalizer.NormalizeDownloadPath(filePath).TrimStart('\\');
        var pathEnd = filePath.LastIndexOf('\\');
        var directoryPath = pathEnd < 0 ?
                                string.Empty :
                                filePath[..pathEnd].Replace('\\', '/');
        var uri = new Uri($"{downloadBaseUrl}/{directoryPath}/{GetFileKey(filePath)}");
        return CDNLinkSigner.Sign(uri);
    }

    public enum InstallTaskState
    {
        NotStarted,
        Connecting,
        Downloading,
        Complete
    }

    private sealed class DownloadProgressState
    (
        long total
    )
    {
        public long Downloaded;
        public long Total { get; } = total;
    }

    public delegate void OnInstallProgressDelegate
    (
        int              index,
        long             fileProgress,
        long             fileTotal,
        long             totalProgress,
        long             total,
        InstallTaskState state
    );

    public delegate void OnVerifyProgressDelegate
    (
        int  index,
        int  count,
        long progress,
        long max
    );

    public event OnInstallProgressDelegate? OnInstallProgress;

    public event OnVerifyProgressDelegate? OnVerifyProgress;

    #region Constants

    private const int    DEFAULT_PROGRESS_REPORT_INTERVAL = 250;
    private const int    DOWNLOAD_ATTEMPT_COUNT           = 3;
    private const int    FILE_STREAM_BUFFER_SIZE          = 131072;
    private const string TEMP_EXTENSION                   = ".tmp";
    private const string USER_AGENT                       = "FF14v3autopatch";

    #endregion
}
