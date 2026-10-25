using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using Serilog;
using XIVLauncher.Common.Http;

namespace XIVLauncher.Common.Util;

public sealed class HttpClientDownloadWithProgress : IDisposable
{
    private const int  BUFFER_SIZE            = 64 * 1024;
    private const int  MAX_PARALLEL_DOWNLOADS = 8;
    private const int  MAX_ATTEMPTS           = 4;
    private const long SEGMENT_SIZE           = 2L << 20;

    private static readonly HttpClient    SharedHttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim DownloadSlots    = new(MAX_PARALLEL_DOWNLOADS);
    private static readonly TimeSpan      RequestTimeout   = TimeSpan.FromSeconds(15);

    private readonly HttpClient                   httpClient;
    private readonly (string Url, string Path)[]  downloads;
    private readonly IDictionary<string, string>? headers;
    private readonly long[]                       downloadedSizes;
    private readonly long[]                       totalSizes;
    private readonly Lock                         progressLock         = new();
    private readonly CancellationTokenSource      lifetimeCancellation = new();
    private          long                         lastProgressTick;

    public HttpClientDownloadWithProgress
    (
        IReadOnlyList<(string Url, string Path)> downloads,
        HttpClient?                              httpClient = null,
        IDictionary<string, string>?             headers    = null
    )
    {
        if (downloads.Count == 0)
            throw new ArgumentException("下载列表不能为空", nameof(downloads));

        this.downloads  = [.. downloads];
        this.httpClient = httpClient ?? SharedHttpClient;
        this.headers    = headers;
        downloadedSizes = new long[downloads.Count];
        totalSizes      = [.. Enumerable.Repeat(-1L, downloads.Count)];
    }

    public async Task Download
    (
        CancellationToken cancellationToken = default
    )
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeCancellation.Token);
        await Parallel.ForEachAsync
        (
            Enumerable.Range(0, downloads.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken      = cancellation.Token
            },
            (index, token) => new ValueTask(DownloadFile(index, token))
        ).ConfigureAwait(false);
    }

    private async Task DownloadFile
    (
        int               index,
        CancellationToken cancellationToken
    )
    {
        var directory = Path.GetDirectoryName(downloads[index].Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var file = new FileStream
        (
            downloads[index].Path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            1,
            FileOptions.Asynchronous | FileOptions.RandomAccess
        );

        for (var attempt = 0; attempt < MAX_ATTEMPTS; attempt++)
        {
            await DownloadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            var rangesStarted = false;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);
                using var request = CreateRequest(downloads[index].Url);
                request.Headers.Range = new RangeHeaderValue(0, SEGMENT_SIZE - 1);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                await response.EnsureSuccessWithDiagnosticsAsync(timeout.Token).ConfigureAwait(false);

                var range   = response.Content.Headers.ContentRange;
                var partial = response.StatusCode == HttpStatusCode.PartialContent;
                var size = partial ?
                               range?.Length                          ?? -1 :
                               response.Content.Headers.ContentLength ?? -1;
                if (partial && (size <= 0 || range?.From != 0 || range.To != Math.Min(SEGMENT_SIZE, size) - 1))
                    throw new InvalidDataException($"服务器返回了不匹配的分段: {range}");

                if (response.Content.Headers.ContentEncoding.Any(encoding => !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("更新源返回了编码后的文件内容，无法验证下载字节范围");

                lock (progressLock)
                {
                    totalSizes[index]      = size;
                    downloadedSizes[index] = 0;
                }

                file.SetLength(Math.Max(size, 0));
                var expectedLength = partial ?
                                         range!.To!.Value + 1 :
                                         size;

                if (!partial || expectedLength == size)
                {
                    await CopyResponse(index, response, file.SafeFileHandle, 0, expectedLength, timeout, cancellationToken).ConfigureAwait(false);
                    ReportProgress(true);
                    return;
                }

                RangeConditionHeaderValue? validator = null;
                if (response.Headers.ETag is { IsWeak: false } entityTag)
                    validator = new RangeConditionHeaderValue(entityTag);
                else if (response.Content.Headers.LastModified is { } lastModified)
                    validator = new RangeConditionHeaderValue(lastModified);

                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                using var rangesCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var       partCount          = (int)((size + SEGMENT_SIZE - 1) / SEGMENT_SIZE);
                var       fileHandle         = file.SafeFileHandle;
                var       rangesToken        = rangesCancellation.Token;
                var       cancelAll          = rangesCancellation.CancelAsync;
                rangesStarted = true;
                var initialDownload = DownloadRange(index, fileHandle, 0, expectedLength - 1, size, validator, cancelAll, response, rangesToken);
                var nextPart        = 0;
                var workers = Enumerable.Range(0, Math.Min(partCount - 1, MAX_PARALLEL_DOWNLOADS))
                                        .Select
                                        (async _ =>
                                            {
                                                while (true)
                                                {
                                                    rangesToken.ThrowIfCancellationRequested();
                                                    var part = Interlocked.Increment(ref nextPart);
                                                    if (part >= partCount)
                                                        return;
                                                    var start = part * SEGMENT_SIZE;
                                                    var end   = Math.Min(start + SEGMENT_SIZE, size) - 1;
                                                    await DownloadRange(index, fileHandle, start, end, size, validator, cancelAll, null, rangesToken).ConfigureAwait
                                                        (false);
                                                }
                                            }
                                        );
                await Task.WhenAll(workers.Prepend(initialDownload)).ConfigureAwait(false);
                ReportProgress(true);
                return;
            }
            catch (Exception ex) when (!rangesStarted && attempt + 1 < MAX_ATTEMPTS && IsTransient(ex, cancellationToken))
            {
                Log.Warning(ex, "[Download] 下载中断，将重试首段: {Url}", downloads[index].Url);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("更新源未在规定时间内返回响应", ex);
            }
            finally
            {
                if (!rangesStarted)
                    DownloadSlots.Release();
            }

            await Task.Delay(GetRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
        }

    }

    private async Task DownloadRange
    (
        int                        index,
        SafeFileHandle             file,
        long                       start,
        long                       end,
        long                       size,
        RangeConditionHeaderValue? validator,
        Func<Task>                 cancelAll,
        HttpResponseMessage?       initialResponse,
        CancellationToken          cancellationToken
    )
    {
        var position = new StrongBox<long>(start);

        for (var attempt = 0; attempt < MAX_ATTEMPTS; attempt++)
        {
            if (initialResponse == null)
                await DownloadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);
                using var request = CreateRequest(downloads[index].Url);
                request.Headers.Range   = new RangeHeaderValue(position.Value, end);
                request.Headers.IfRange = validator;
                using var response = initialResponse ??
                                     await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                initialResponse = null;
                await response.EnsureSuccessWithDiagnosticsAsync(timeout.Token).ConfigureAwait(false);
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != position.Value || range.To != end || range.Length != size)
                    throw new InvalidDataException($"更新源文件已变化或返回了不匹配的分段: {range}");
                if (response.Content.Headers.ContentEncoding.Any(encoding => !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("更新源返回了编码后的分段内容");
                if (validator?.EntityTag is { } expectedTag && !expectedTag.Equals(response.Headers.ETag))
                    throw new InvalidDataException("更新源在分段下载期间更改了文件标识");

                await CopyResponse
                (
                    index,
                    response,
                    file,
                    position.Value,
                    end - position.Value + 1,
                    timeout,
                    cancellationToken,
                    bytes => position.Value += bytes
                ).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt + 1 < MAX_ATTEMPTS && IsTransient(ex, cancellationToken))
            {
                if (validator == null)
                {
                    lock (progressLock)
                        downloadedSizes[index] -= position.Value - start;
                    position.Value = start;
                }

                Log.Warning(ex, "[Download] 分段传输中断，将从 {Position} 继续: {Url}", position.Value, downloads[index].Url);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                await cancelAll().ConfigureAwait(false);
                throw new TimeoutException("更新源未在规定时间内返回分段响应", ex);
            }
            catch
            {
                await cancelAll().ConfigureAwait(false);
                throw;
            }
            finally
            {
                DownloadSlots.Release();
            }

            if (position.Value > end)
                return;
            await Task.Delay(GetRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CopyResponse
    (
        int                     index,
        HttpResponseMessage     response,
        SafeFileHandle          file,
        long                    offset,
        long                    expectedLength,
        CancellationTokenSource timeout,
        CancellationToken       cancellationToken,
        Action<int>?            recordBytes = null
    )
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);

        try
        {
            await using var stream   = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            long            received = 0;

            while (true)
            {
                timeout.CancelAfter(RequestTimeout);
                var read = await stream.ReadAsync(buffer.AsMemory(0, BUFFER_SIZE), timeout.Token).ConfigureAwait(false);
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                if (read == 0)
                    break;
                if (expectedLength >= 0 && read > expectedLength - received)
                    throw new InvalidDataException("更新源返回了超出请求范围的内容");

                await RandomAccess.WriteAsync(file, buffer.AsMemory(0, read), offset + received, cancellationToken).ConfigureAwait(false);
                received += read;
                recordBytes?.Invoke(read);
                lock (progressLock)
                    downloadedSizes[index] += read;
                ReportProgress(false);
            }

            if (expectedLength < 0)
            {
                lock (progressLock)
                    totalSizes[index] = received;
                return;
            }

            if (received != expectedLength)
                throw new IOException($"下载内容不完整: 预期 {expectedLength} 字节，实际 {received} 字节");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"更新源连续 {RequestTimeout.TotalSeconds:0} 秒未传输数据", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ReportProgress
    (
        bool force
    )
    {
        lock (progressLock)
        {
            var now = Environment.TickCount64;
            if (!force && now - lastProgressTick < 200)
                return;
            lastProgressTick = now;
            var downloaded = downloadedSizes.Sum();
            long? total = totalSizes.All(size => size >= 0) ?
                              totalSizes.Sum() :
                              null;
            double? percent = total > 0 ?
                                  Math.Round((double)downloaded / total.Value * 100, 2) :
                                  null;
            ProgressChanged?.Invoke(total, downloaded, percent);
        }
    }

    private HttpRequestMessage CreateRequest
    (
        string url
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version       = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        if (headers != null)
        {
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.ParseAdd("identity");
        return request;
    }

    private static bool IsTransient
    (
        Exception         exception,
        CancellationToken cancellationToken
    ) =>
        !cancellationToken.IsCancellationRequested &&
        exception switch
        {
            InvalidDataException => false,
            HttpRequestException { StatusCode: { } status } => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500,
            HttpRequestException or IOException or OperationCanceledException or TimeoutException => true,
            _ => false
        };

    private static TimeSpan GetRetryDelay
    (
        int attempt
    ) =>
        TimeSpan.FromMilliseconds((500 << attempt) + Random.Shared.Next(250));

    private static HttpClient CreateHttpClient()
    {
        var client = XLHttpClientFactory.Create(TimeSpan.FromSeconds(5), MAX_PARALLEL_DOWNLOADS, DecompressionMethods.None);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");
        return client;
    }

    public void Dispose()
    {
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
    }

    public delegate void ProgressChangedHandler
    (
        long?   totalFileSize,
        long    totalBytesDownloaded,
        double? progressPercentage
    );

    public event ProgressChangedHandler? ProgressChanged;
}
