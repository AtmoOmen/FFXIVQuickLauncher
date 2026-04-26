using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
    private readonly HttpClient httpClient;
    private readonly string     downloadUrl;
    private readonly string     destinationFilePath;
    
    private int parallelParts = 32;

    public HttpClientDownloadWithProgress
    (
        string downloadUrl,
        string destinationFilePath
    )
    {
        this.downloadUrl         = downloadUrl;
        this.destinationFilePath = destinationFilePath;

        httpClient         = XLHttpClientFactory.Create(TimeSpan.FromSeconds(5), 20, DecompressionMethods.All);
        httpClient.Timeout = TimeSpan.FromMinutes(10);
        httpClient.DefaultRequestHeaders.Add("User-Agent", PlatformHelpers.GetVersion());
    }

    #region Disposal

    public void Dispose() =>
        httpClient?.Dispose();

    #endregion

    public async Task Download(bool isNuGet = false)
    {
        parallelParts = Environment.ProcessorCount;
        Log.Information("[DUPDATE] 下载线程数: {0}", Environment.ProcessorCount);

        var probeTotalSize = await ProbeRangeSupport(isNuGet).ConfigureAwait(false);

        if (probeTotalSize > 0)
        {
            await DownloadSegmented(probeTotalSize.Value, isNuGet).ConfigureAwait(false);
            return;
        }

        using var response = await SendWithRetry
                             (
                                 () =>
                                 {
                                     var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);

                                     if (isNuGet)
                                     {
                                         request.Headers.Add("User-Agent",             "NuGet VS VSIX/6.14.0 (WINDOWS, Community/17.0)");
                                         request.Headers.Add("X-NuGet-Client-Version", "6.14.0");
                                         request.Headers.Add("X-NuGet-Session-Id",     Guid.NewGuid().ToString("D"));
                                     }

                                     return request;
                                 },
                                 HttpCompletionOption.ResponseHeadersRead
                             ).ConfigureAwait(false);
        await DownloadFileFromHttpResponseMessage(response).ConfigureAwait(false);
    }

    private static bool IsRetryableStatus(HttpStatusCode code)
    {
        if (code == HttpStatusCode.RequestTimeout)
            return true;
        var n = (int)code;
        return n >= 500 && n <= 599;
    }

    private async Task DownloadFileFromHttpResponseMessage(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;

        using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await ProcessContentStream(totalBytes, contentStream).ConfigureAwait(false);
    }

    private async Task ProcessContentStream(long? totalDownloadSize, Stream contentStream)
    {
        var totalBytesRead   = 0L;
        var readCount        = 0L;
        var buffer           = new byte[8192];
        var isMoreToRead     = true;
        var lastProgressTick = Environment.TickCount64;

        using var fileStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        do
        {
            var bytesRead = await contentStream.ReadAsync(buffer).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                isMoreToRead = false;
                TriggerProgressChanged(totalDownloadSize, totalBytesRead);
                continue;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);

            totalBytesRead += bytesRead;
            readCount      += 1;

            if (readCount % 100 == 0)
                TriggerProgressChanged(totalDownloadSize, totalBytesRead);
            var now = Environment.TickCount64;

            if (now - lastProgressTick >= 500)
            {
                TriggerProgressChanged(totalDownloadSize, totalBytesRead);
                lastProgressTick = now;
            }
        }
        while (isMoreToRead);
    }

    private async Task<long?> ProbeRangeSupport(bool isNuGet)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            request.Headers.Range = new RangeHeaderValue(0, 0); // 请求第一个字节

            if (isNuGet)
            {
                request.Headers.Add("User-Agent",             "NuGet VS VSIX/6.14.0 (WINDOWS, Community/17.0)");
                request.Headers.Add("X-NuGet-Client-Version", "6.14.0");
                request.Headers.Add("X-NuGet-Session-Id",     Guid.NewGuid().ToString("D"));
            }

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.Length != null)
                return response.Content.Headers.ContentRange.Length.Value;
        }
        catch (Exception ex)
        {
            Log.Warning("[DUPDATE] [{url}] 探测 Range 支持失败或超时: {Msg}. 将降级为直接下载。", downloadUrl, ex.Message);
        }

        return null;
    }

    private async Task DownloadSegmented(long totalSize, bool isNuGet)
    {
        var prealloc = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1, true);
        prealloc.SetLength(totalSize);
        await prealloc.FlushAsync().ConfigureAwait(false);
        prealloc.Dispose();

        var totalBytesRead   = 0L;
        var readCount        = 0L;
        var lastProgressTick = Environment.TickCount64;

        var chunks    = BuildChunks(totalSize);
        var queue     = new ConcurrentQueue<(long Start, long End)>(chunks);
        var remaining = chunks.Count;

        var tasks = new List<Task>(parallelParts);

        for (var i = 0; i < parallelParts; i++)
        {
            tasks.Add
            (
                Task.Run
                (async () =>
                    {
                        using var fileStream = new FileStream(destinationFilePath, FileMode.Open, FileAccess.Write, FileShare.Write, 65536, true);
                        var       buffer     = new byte[65536];

                        while (true)
                        {
                            if (!queue.TryDequeue(out var chunk))
                            {
                                if (Volatile.Read(ref remaining) == 0)
                                    break;
                                await Task.Delay(25).ConfigureAwait(false);
                                continue;
                            }

                            using var resp = await SendWithRetry
                                             (
                                                 () =>
                                                 {
                                                     var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                                                     req.Headers.Range = new RangeHeaderValue(chunk.Start, chunk.End);

                                                     if (isNuGet)
                                                     {
                                                         req.Headers.Add("User-Agent",             "NuGet VS VSIX/6.14.0 (WINDOWS, Community/17.0)");
                                                         req.Headers.Add("X-NuGet-Client-Version", "6.14.0");
                                                         req.Headers.Add("X-NuGet-Session-Id",     Guid.NewGuid().ToString("D"));
                                                     }

                                                     return req;
                                                 },
                                                 HttpCompletionOption.ResponseHeadersRead
                                             ).ConfigureAwait(false);

                            resp.EnsureSuccessStatusCode();
                            using var contentStream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                            fileStream.Seek(chunk.Start, SeekOrigin.Begin);

                            while (true)
                            {
                                var bytesRead = await contentStream.ReadAsync(buffer).ConfigureAwait(false);
                                if (bytesRead == 0)
                                    break;
                                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                                Interlocked.Add(ref totalBytesRead, bytesRead);
                                var rc = Interlocked.Increment(ref readCount);
                                if (rc % 100 == 0)
                                    TriggerProgressChanged(totalSize, Interlocked.Read(ref totalBytesRead));
                                var now = Environment.TickCount64;

                                if (now - lastProgressTick >= 500)
                                {
                                    TriggerProgressChanged(totalSize, Interlocked.Read(ref totalBytesRead));
                                    lastProgressTick = now;
                                }
                            }

                            Interlocked.Decrement(ref remaining);
                        }
                    }
                )
            );
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        TriggerProgressChanged(totalSize, Interlocked.Read(ref totalBytesRead));
    }

    private void TriggerProgressChanged(long? totalDownloadSize, long totalBytesRead)
    {
        if (ProgressChanged == null)
            return;

        double? progressPercentage = null;
        if (totalDownloadSize.HasValue)
            progressPercentage = Math.Round((double)totalBytesRead / totalDownloadSize.Value * 100, 2);

        ProgressChanged(totalDownloadSize, totalBytesRead, progressPercentage);
    }

    private List<(long Start, long End)> BuildChunks(long totalSize)
    {
        const long MIN_CHUNK = 1L << 20;
        const long MAX_CHUNK = 8L << 20;

        var targetChunks = parallelParts * 64;
        var result       = new List<(long Start, long End)>();
        var estimated    = Math.Max(MIN_CHUNK, totalSize / targetChunks);
        var chunkSize    = Math.Min(estimated, MAX_CHUNK);

        long pos = 0;

        while (pos < totalSize)
        {
            var end = Math.Min(pos + chunkSize - 1, totalSize - 1);
            result.Add((pos, end));
            pos = end + 1;
        }

        return result;
    }

    private async Task<HttpResponseMessage> SendWithRetry(Func<HttpRequestMessage> requestFactory, HttpCompletionOption completionOption, int maxRetries = 3)
    {
        var delay = TimeSpan.FromSeconds(1);

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            var request = requestFactory();

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
                if (attempt < maxRetries)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 8));
                    continue;
                }

                throw;
            }
            catch (TaskCanceledException)
            {
                if (attempt < maxRetries)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 8));
                    continue;
                }

                throw;
            }
        }

        throw new InvalidOperationException("Send retry attempts exhausted");
    }

    public delegate void ProgressChangedHandler(long? totalFileSize, long totalBytesDownloaded, double? progressPercentage);

    public event ProgressChangedHandler? ProgressChanged;
}
