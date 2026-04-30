using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Http;

namespace XIVLauncher.Common.Util;

public class HttpClientDownloadWithProgress : IDisposable
{
    private readonly HttpClient                  httpClient;
    private readonly (string Url, string Path)[] downloads;
    private readonly long[]                      actualDownloadedSizes;
    private readonly long[]                      reportedSizes;
    private readonly long[]                      totalSizes;

    private long totalDownloadedSize;
    private long totalSize;

    public HttpClientDownloadWithProgress(IReadOnlyList<(string Url, string Path)> downloads)
    {
        if (downloads.Count == 0)
            throw new ArgumentException("下载列表不能为空", nameof(downloads));

        this.downloads = downloads.ToArray();

        actualDownloadedSizes = new long[downloads.Count];
        reportedSizes         = new long[downloads.Count];
        totalSizes            = new long[downloads.Count];

        httpClient         = XLHttpClientFactory.Create(TimeSpan.FromSeconds(5), MAX_CONNECTIONS_PER_SERVER, DecompressionMethods.All);
        httpClient.Timeout = TimeSpan.FromMinutes(10);
        httpClient.DefaultRequestHeaders.Add("User-Agent", PlatformHelpers.GetVersion());
    }

    #region Disposal

    public void Dispose() =>
        httpClient?.Dispose();

    #endregion

    public async Task Download()
    {
        var probes = await ProbeDownloads().ConfigureAwait(false);

        for (var i = 0; i < probes.Length; i++)
        {
            if (probes[i].Size <= 0)
                continue;

            totalSizes[i] =  probes[i].Size!.Value;
            totalSize     += probes[i].Size!.Value;
        }

        var tasks = new List<Task>(downloads.Length);

        for (var i = 0; i < downloads.Length; i++)
            tasks.Add(Download(i, probes[i].Size, probes[i].SupportsRanges, probes[i].EntityTag, probes[i].LastModified));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<(long? Size, bool SupportsRanges, EntityTagHeaderValue? EntityTag, DateTimeOffset? LastModified)[]> ProbeDownloads()
    {
        var tasks = new Task<(long? Size, bool SupportsRanges, EntityTagHeaderValue? EntityTag, DateTimeOffset? LastModified)>[downloads.Length];

        for (var i = 0; i < downloads.Length; i++)
            tasks[i] = ProbeDownload(downloads[i].Url);

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task Download(int index, long? totalDownloadSize, bool supportsRanges, EntityTagHeaderValue? entityTag, DateTimeOffset? lastModified)
    {
        var download = downloads[index];
        var isNuGet  = IsNuGetDownload(download.Url);

        if (supportsRanges && totalDownloadSize is >= MIN_SEGMENTED_DOWNLOAD_SIZE)
        {
            await DownloadSegmented(index, totalDownloadSize.Value, isNuGet, entityTag, lastModified).ConfigureAwait(false);
            return;
        }

        using var response = await SendWithRetry
                             (
                                 () => CreateRequest(download.Url, isNuGet),
                                 HttpCompletionOption.ResponseHeadersRead
                             ).ConfigureAwait(false);
        await DownloadFileFromHttpResponseMessage(index, response).ConfigureAwait(false);
    }

    private async Task DownloadFileFromHttpResponseMessage(int index, HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();

        var totalDownloadSize = response.Content.Headers.ContentLength;
        if (totalDownloadSize > 0 && Interlocked.CompareExchange(ref totalSizes[index], totalDownloadSize.Value, 0) == 0) Interlocked.Add(ref totalSize, totalDownloadSize.Value);

        using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await ProcessContentStream(index, contentStream).ConfigureAwait(false);
    }

    private async Task ProcessContentStream(int index, Stream contentStream)
    {
        var buffer           = new byte[BUFFER_SIZE];
        var lastProgressTick = Environment.TickCount64;
        var readCount        = 0L;

        PrepareDestinationDirectory(downloads[index].Path);

        await using var fileStream = new FileStream(downloads[index].Path, FileMode.Create, FileAccess.Write, FileShare.None, BUFFER_SIZE, true);

        while (true)
        {
            var bytesRead = await contentStream.ReadAsync(buffer).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                TriggerProgressChanged(index, actualDownloadedSizes[index]);
                return;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
            var downloaded = Interlocked.Add(ref actualDownloadedSizes[index], bytesRead);
            readCount++;

            if (readCount % 100 == 0)
                TriggerProgressChanged(index, downloaded);

            var now = Environment.TickCount64;

            if (now - lastProgressTick >= PROGRESS_INTERVAL_MILLISECONDS)
            {
                TriggerProgressChanged(index, downloaded);
                lastProgressTick = now;
            }
        }
    }

    private async Task<(long? Size, bool SupportsRanges, EntityTagHeaderValue? EntityTag, DateTimeOffset? LastModified)> ProbeDownload(string url)
    {
        try
        {
            var       isNuGet = IsNuGetDownload(url);
            using var request = CreateRequest(url, isNuGet);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.Length != null)
                return (response.Content.Headers.ContentRange.Length.Value, true, response.Headers.ETag, response.Content.Headers.LastModified);

            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength > 0)
                return (response.Content.Headers.ContentLength.Value, false, response.Headers.ETag, response.Content.Headers.LastModified);
        }
        catch (Exception ex)
        {
            Log.Warning("[DUPDATE] [{url}] 探测 Range 支持失败或超时: {Msg}. 将降级为直接下载。", url, ex.Message);
        }

        return (null, false, null, null);
    }

    private async Task DownloadSegmented(int index, long downloadSize, bool isNuGet, EntityTagHeaderValue? entityTag, DateTimeOffset? lastModified)
    {
        var parallelParts = Math.Clamp((int)(downloadSize / MIN_SEGMENTED_DOWNLOAD_SIZE), MIN_SEGMENTED_PARTS, MAX_SEGMENTED_PARTS);
        Log.Information("[DUPDATE] 下载线程数: {0}, 单片大小: {1} MiB", parallelParts, SEGMENT_SIZE >> 20);

        PrepareDestinationDirectory(downloads[index].Path);

        await using (var prealloc = new FileStream(downloads[index].Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1, true))
        {
            prealloc.SetLength(downloadSize);
            await prealloc.FlushAsync().ConfigureAwait(false);
        }

        var chunks = BuildChunks(downloadSize);
        var queue  = new ConcurrentQueue<(long Start, long End)>(chunks);
        var tasks  = new List<Task>(parallelParts);

        for (var i = 0; i < parallelParts; i++)
            tasks.Add(DownloadSegments(index, downloadSize, isNuGet, entityTag, lastModified, queue));

        await Task.WhenAll(tasks).ConfigureAwait(false);
        TriggerProgressChanged(index, actualDownloadedSizes[index]);
    }

    private async Task DownloadSegments(int index, long downloadSize, bool isNuGet, EntityTagHeaderValue? entityTag, DateTimeOffset? lastModified, ConcurrentQueue<(long Start, long End)> queue)
    {
        var buffer           = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
        var lastProgressTick = Environment.TickCount64;
        var readCount        = 0L;

        await using var fileStream = new FileStream(downloads[index].Path, FileMode.Open, FileAccess.Write, FileShare.Write, BUFFER_SIZE, FileOptions.Asynchronous | FileOptions.RandomAccess);

        try
        {
            while (queue.TryDequeue(out var chunk))
            {
                using var response = await SendWithRetry
                                     (
                                         () =>
                                         {
                                             var request = CreateRequest(downloads[index].Url, isNuGet);
                                             request.Headers.Range = new RangeHeaderValue(chunk.Start, chunk.End);

                                             if (entityTag is not null && !entityTag.IsWeak)
                                                 request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
                                             else if (lastModified is not null)
                                                 request.Headers.IfRange = new RangeConditionHeaderValue(lastModified.Value);

                                             return request;
                                         },
                                         HttpCompletionOption.ResponseHeadersRead
                                     ).ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.PartialContent)
                    throw new HttpRequestException($"服务器未返回分段内容: {response.StatusCode}");

                var range = response.Content.Headers.ContentRange;

                if (range?.From != chunk.Start || range.To != chunk.End || range.Length != downloadSize)
                    throw new HttpRequestException($"服务器返回了不匹配的分段: {range}");

                using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var       position      = chunk.Start;

                while (true)
                {
                    var bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, BUFFER_SIZE)).ConfigureAwait(false);

                    if (bytesRead == 0)
                        break;

                    await RandomAccess.WriteAsync(fileStream.SafeFileHandle, buffer.AsMemory(0, bytesRead), position).ConfigureAwait(false);
                    position += bytesRead;
                    var downloaded = Interlocked.Add(ref actualDownloadedSizes[index], bytesRead);
                    readCount++;

                    if (readCount % 100 == 0)
                        TriggerProgressChanged(index, downloaded);

                    var now = Environment.TickCount64;

                    if (now - lastProgressTick >= PROGRESS_INTERVAL_MILLISECONDS)
                    {
                        TriggerProgressChanged(index, downloaded);
                        lastProgressTick = now;
                    }
                }

                if (position != chunk.End + 1)
                    throw new IOException($"分段下载不完整: {chunk.Start}-{chunk.End}, 已下载到 {position - 1}");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void TriggerProgressChanged(int index, long fileDownloaded)
    {
        if (ProgressChanged == null)
            return;

        var fileTotal = totalSizes[index];
        if (fileTotal > 0)
            fileDownloaded = Math.Min(fileDownloaded, fileTotal);

        var     lastFileDownloaded = Interlocked.Exchange(ref reportedSizes[index], fileDownloaded);
        var     totalDownloaded    = Interlocked.Add(ref totalDownloadedSize, fileDownloaded - lastFileDownloaded);
        var     knownTotal         = Interlocked.Read(ref totalSize);
        long?   reportTotal        = HasUnknownSize() ? null : knownTotal;
        double? progress           = reportTotal > 0 ? Math.Round((double)totalDownloaded / reportTotal.Value * 100, 2) : null;

        ProgressChanged(reportTotal, totalDownloaded, progress);
    }

    private bool HasUnknownSize()
    {
        for (var i = 0; i < totalSizes.Length; i++)
        {
            if (Interlocked.Read(ref totalSizes[i]) <= 0)
                return true;
        }

        return false;
    }

    private static bool IsRetryableStatus(HttpStatusCode code)
    {
        if (code == HttpStatusCode.RequestTimeout)
            return true;

        var n = (int)code;
        return n is >= 500 and <= 599;
    }

    private static HttpRequestMessage CreateRequest(string url, bool isNuGet)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (!isNuGet)
            return request;

        request.Headers.Add("User-Agent",             "NuGet VS VSIX/6.14.0 (WINDOWS, Community/17.0)");
        request.Headers.Add("X-NuGet-Client-Version", "6.14.0");
        request.Headers.Add("X-NuGet-Session-Id",     Guid.NewGuid().ToString("D"));

        return request;
    }

    private static bool IsNuGetDownload(string url) =>
        url.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase);

    private static void PrepareDestinationDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private static List<(long Start, long End)> BuildChunks(long downloadSize)
    {
        var result = new List<(long Start, long End)>();

        long position = 0;

        while (position < downloadSize)
        {
            var end = Math.Min(position + SEGMENT_SIZE - 1, downloadSize - 1);
            result.Add((position, end));
            position = end + 1;
        }

        return result;
    }

    private async Task<HttpResponseMessage> SendWithRetry(Func<HttpRequestMessage> requestFactory, HttpCompletionOption completionOption, int maxRetries = 3)
    {
        var delay = TimeSpan.FromSeconds(1);

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            using var request = requestFactory();

            try
            {
                var response = await httpClient.SendAsync(request, completionOption).ConfigureAwait(false);

                if (IsRetryableStatus(response.StatusCode) && attempt < maxRetries)
                {
                    response.Dispose();
                    await Task.Delay(delay).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 8));
                    continue;
                }

                return response;
            }
            catch (HttpRequestException)
            {
                if (attempt >= maxRetries)
                    throw;

                await Task.Delay(delay).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 8));
            }
            catch (TaskCanceledException)
            {
                if (attempt >= maxRetries)
                    throw;

                await Task.Delay(delay).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 8));
            }
        }

        throw new InvalidOperationException("Send retry attempts exhausted");
    }

    public delegate void ProgressChangedHandler(long? totalFileSize, long totalBytesDownloaded, double? progressPercentage);

    public event ProgressChangedHandler? ProgressChanged;

    #region Constants

    private const int BUFFER_SIZE = 64 * 1024;

    private const int MAX_CONNECTIONS_PER_SERVER = 128;

    private const int MAX_SEGMENTED_PARTS = 32;

    private const int MIN_SEGMENTED_PARTS = 4;

    private const int PROGRESS_INTERVAL_MILLISECONDS = 500;

    private const long MIN_SEGMENTED_DOWNLOAD_SIZE = 16L << 20;

    private const long SEGMENT_SIZE = 4L << 20;

    #endregion
}
