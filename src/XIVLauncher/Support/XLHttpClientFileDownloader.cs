using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Velopack.Sources;
using XIVLauncher.Common.Http;

namespace XIVLauncher.Support;

public class XLHttpClientFileDownloader : IFileDownloader
{
    private const int BUFFER_SIZE = 64 * 1024;

    private static readonly TimeSpan ConnectTimeout             = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MetadataRequestTimeout     = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultFileDownloadTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinimumFileDownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StreamIdleTimeout          = TimeSpan.FromSeconds(45);

    private readonly HttpClient httpClient;

    public XLHttpClientFileDownloader()
    {
        httpClient = new HttpClient
        (
            new SocketsHttpHandler
            {
                UseProxy                       = true,
                ConnectTimeout                 = ConnectTimeout,
                MaxConnectionsPerServer        = 50,
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime       = TimeSpan.FromMinutes(1),
                Expect100ContinueTimeout       = TimeSpan.Zero,
                AutomaticDecompression         = DecompressionMethods.All,
                ConnectCallback                = HappyEyeballsCallback.ConnectCallback
            }
        );
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task DownloadFile
    (
        string            url,
        string            targetFile,
        Action<int>       progress,
        string?           authorization = null,
        string?           accept        = null,
        double            timeout       = 30,
        CancellationToken cancelToken   = default
    )
    {
        var totalTimeout = GetFileDownloadTimeout(timeout);

        using var totalTimeoutCts = new CancellationTokenSource(totalTimeout);
        using var requestCts      = CancellationTokenSource.CreateLinkedTokenSource(cancelToken, totalTimeoutCts.Token);
        using var streamIdleCts   = new CancellationTokenSource();
        using var readCts         = CancellationTokenSource.CreateLinkedTokenSource(cancelToken, totalTimeoutCts.Token, streamIdleCts.Token);

        byte[]? buffer = null;

        try
        {
            using var request = CreateRequest(HttpMethod.Get, url, authorization, accept);
            using var response = await httpClient.SendAsync
                                 (
                                     request,
                                     HttpCompletionOption.ResponseHeadersRead,
                                     requestCts.Token
                                 );

            response.EnsureSuccessStatusCode();

            var totalBytes        = response.Content.Headers.ContentLength ?? -1L;
            var canReportProgress = totalBytes > 0;

            using var contentStream = await response.Content.ReadAsStreamAsync(requestCts.Token);

            var directory = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            using var fileStream = new FileStream
            (
                targetFile,
                new FileStreamOptions
                {
                    Mode       = FileMode.Create,
                    Access     = FileAccess.Write,
                    Share      = FileShare.None,
                    BufferSize = BUFFER_SIZE,
                    Options    = FileOptions.Asynchronous | FileOptions.SequentialScan
                }
            );

            buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);

            long totalRead    = 0;
            var  lastProgress = -1;

            while (true)
            {
                streamIdleCts.CancelAfter(StreamIdleTimeout);
                var bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, BUFFER_SIZE), readCts.Token);

                if (bytesRead <= 0)
                    break;

                streamIdleCts.CancelAfter(StreamIdleTimeout);
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), requestCts.Token);

                if (!canReportProgress)
                    continue;

                totalRead += bytesRead;
                var currentProgress = (int)(totalRead * 100 / totalBytes);

                if (currentProgress <= lastProgress)
                    continue;

                lastProgress = currentProgress;
                progress?.Invoke(currentProgress);
            }

            if (canReportProgress && lastProgress != 100)
                progress?.Invoke(100);
        }
        catch (Exception ex)
        {
            var normalizedException = NormalizeFileDownloadException(ex, totalTimeout, cancelToken, totalTimeoutCts, streamIdleCts);
            Log.Error(normalizedException, "下载文件失败：{Url} -> {TargetFile}", url, targetFile);

            if (File.Exists(targetFile))
            {
                try
                {
                    File.Delete(targetFile);
                }
                catch
                {
                    // 删除临时文件失败不影响后续错误处理。
                }
            }

            if (ReferenceEquals(normalizedException, ex))
                throw;

            throw normalizedException;
        }
        finally
        {
            if (buffer != null)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<byte[]> DownloadBytes
    (
        string  url,
        string? authorization = null,
        string? accept        = null,
        double  timeout       = 30
    )
    {
        var       requestTimeout = GetMetadataRequestTimeout(timeout);
        using var cts            = new CancellationTokenSource(requestTimeout);

        try
        {
            using var request  = CreateRequest(HttpMethod.Get, url, authorization, accept);
            using var response = await httpClient.SendAsync(request, cts.Token);

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch (Exception ex)
        {
            var normalizedException = NormalizeMetadataException(ex, requestTimeout, cts);
            Log.Error(normalizedException, "下载字节内容失败：{Url}", url);

            if (ReferenceEquals(normalizedException, ex))
                throw;

            throw normalizedException;
        }
    }

    public async Task<string> DownloadString
    (
        string  url,
        string? authorization = null,
        string? accept        = null,
        double  timeout       = 30
    )
    {
        var       requestTimeout = GetMetadataRequestTimeout(timeout);
        using var cts            = new CancellationTokenSource(requestTimeout);

        try
        {
            using var request  = CreateRequest(HttpMethod.Get, url, authorization, accept);
            using var response = await httpClient.SendAsync(request, cts.Token);

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception ex)
        {
            var normalizedException = NormalizeMetadataException(ex, requestTimeout, cts);
            Log.Error(normalizedException, "下载文本内容失败：{Url}", url);

            if (ReferenceEquals(normalizedException, ex))
                throw;

            throw normalizedException;
        }
    }

    private static TimeSpan GetMetadataRequestTimeout(double timeout) =>
        timeout > 0 ? TimeSpan.FromSeconds(timeout) : MetadataRequestTimeout;

    private static TimeSpan GetFileDownloadTimeout(double timeout)
    {
        if (timeout <= 0)
            return DefaultFileDownloadTimeout;

        var requestedTimeout = TimeSpan.FromSeconds(timeout);
        return requestedTimeout < MinimumFileDownloadTimeout ? MinimumFileDownloadTimeout : requestedTimeout;
    }

    private static Exception NormalizeMetadataException
    (
        Exception               exception,
        TimeSpan                timeout,
        CancellationTokenSource timeoutCts
    )
    {
        if (exception is OperationCanceledException && timeoutCts.IsCancellationRequested)
            return new TimeoutException($"请求超时，请稍后重试。超时阈值：{timeout.TotalSeconds:0} 秒。", exception);

        return exception;
    }

    private static Exception NormalizeFileDownloadException
    (
        Exception               exception,
        TimeSpan                totalTimeout,
        CancellationToken       externalCancellationToken,
        CancellationTokenSource totalTimeoutCts,
        CancellationTokenSource streamIdleCts
    )
    {
        if (exception is not OperationCanceledException)
            return exception;

        if (externalCancellationToken.IsCancellationRequested)
            return exception;

        if (streamIdleCts.IsCancellationRequested)
            return new TimeoutException($"下载连接已中断，连续 {StreamIdleTimeout.TotalSeconds:0} 秒未收到任何数据。", exception);

        if (totalTimeoutCts.IsCancellationRequested)
            return new TimeoutException($"下载超时，{totalTimeout.TotalMinutes:0} 分钟内未完成。", exception);

        return exception;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? authorization, string? accept)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("XIVLauncherCN");

        if (!string.IsNullOrWhiteSpace(accept))
            request.Headers.Accept.ParseAdd(accept);

        if (!string.IsNullOrWhiteSpace(authorization))
        {
            try
            {
                request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
            }
            catch (FormatException)
            {
                Log.Warning("授权头格式无效：{Auth}", authorization);
            }
        }

        return request;
    }
}
