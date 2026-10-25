using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Serilog;
using Velopack.Sources;
using XIVLauncher.Common.Http;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Support;

public class XLHttpClientFileDownloader : IFileDownloader
{
    private static readonly TimeSpan ConnectTimeout             = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MetadataRequestTimeout     = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultFileDownloadTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinimumFileDownloadTimeout = TimeSpan.FromMinutes(10);

    private static readonly HttpClient HTTPClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = XLHttpClientFactory.Create(ConnectTimeout, 8, DecompressionMethods.All);
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    public async Task DownloadFile
    (
        string                       url,
        string                       targetFile,
        Action<int>                  progress,
        IDictionary<string, string>? headers     = null,
        double                       timeout     = 30,
        CancellationToken            cancelToken = default
    )
    {
        var totalTimeout = GetFileDownloadTimeout(timeout);

        using var totalTimeoutCts = new CancellationTokenSource(totalTimeout);
        using var requestCts      = CancellationTokenSource.CreateLinkedTokenSource(cancelToken, totalTimeoutCts.Token);

        try
        {
            using var downloader   = new HttpClientDownloadWithProgress([(url, targetFile)], headers: headers);
            var       lastProgress = -1;
            downloader.ProgressChanged += (_, _, percentage) =>
            {
                if (percentage == null)
                    return;
                var currentProgress = Math.Clamp((int)percentage.Value, 0, 100);
                if (currentProgress <= lastProgress)
                    return;
                lastProgress = currentProgress;
                progress.Invoke(currentProgress);
            };
            await downloader.Download(requestCts.Token);
            if (lastProgress != 100)
                progress.Invoke(100);
        }
        catch (OperationCanceledException ex) when (!cancelToken.IsCancellationRequested && totalTimeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"下载超时，{totalTimeout.TotalMinutes:0} 分钟内未完成。", ex);
        }
    }

    public async Task<byte[]> DownloadBytes
    (
        string                       url,
        IDictionary<string, string>? headers = null,
        double                       timeout = 30
    )
    {
        var       requestTimeout = GetMetadataRequestTimeout(timeout);
        using var cts            = new CancellationTokenSource(requestTimeout);

        try
        {
            using var request  = CreateRequest(HttpMethod.Get, url, headers);
            using var response = await HTTPClient.SendAsync(request, cts.Token);

            await response.EnsureSuccessWithDiagnosticsAsync(cts.Token);
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
        string                       url,
        IDictionary<string, string>? headers = null,
        double                       timeout = 30
    )
    {
        var       requestTimeout = GetMetadataRequestTimeout(timeout);
        using var cts            = new CancellationTokenSource(requestTimeout);

        try
        {
            using var request  = CreateRequest(HttpMethod.Get, url, headers);
            using var response = await HTTPClient.SendAsync(request, cts.Token);

            await response.EnsureSuccessWithDiagnosticsAsync(cts.Token);
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

    private static TimeSpan GetMetadataRequestTimeout
    (
        double timeout
    ) =>
        timeout > 0 ?
            TimeSpan.FromSeconds(timeout) :
            MetadataRequestTimeout;

    private static TimeSpan GetFileDownloadTimeout
    (
        double timeout
    )
    {
        if (timeout <= 0)
            return DefaultFileDownloadTimeout;

        var requestedTimeout = TimeSpan.FromSeconds(timeout);
        return requestedTimeout < MinimumFileDownloadTimeout ?
                   MinimumFileDownloadTimeout :
                   requestedTimeout;
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

    private static HttpRequestMessage CreateRequest
    (
        HttpMethod                   method,
        string                       url,
        IDictionary<string, string>? headers
    )
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("XIVLauncherCN");

        if (headers == null || headers.Count == 0)
            return request;

        foreach (var (key, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    request.Headers.Authorization = AuthenticationHeaderValue.Parse(value);
                }
                catch (FormatException)
                {
                    Log.Warning("授权头格式无效：{Auth}", value);
                }

                continue;
            }

            if (key.Equals("Accept", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Accept.ParseAdd(value);
                continue;
            }

            if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.UserAgent.ParseAdd(value);
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(key, value) && request.Content != null)
                request.Content.Headers.TryAddWithoutValidation(key, value);
        }

        return request;
    }
}
