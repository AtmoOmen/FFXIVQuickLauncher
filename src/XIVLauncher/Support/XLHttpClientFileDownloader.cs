using System;
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
    private readonly HttpClient httpClient;

    public XLHttpClientFileDownloader()
    {
        httpClient = new HttpClient
        (
            new SocketsHttpHandler
            {
                UseProxy                       = true,
                ConnectTimeout                 = TimeSpan.FromSeconds(10),
                MaxConnectionsPerServer        = 50,
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime       = TimeSpan.FromMinutes(1),
                Expect100ContinueTimeout       = TimeSpan.Zero,
                AutomaticDecompression         = DecompressionMethods.All,
                ConnectCallback                = HappyEyeballsCallback.ConnectCallback
            }
        );
        httpClient.Timeout = TimeSpan.FromMinutes(10);
    }

    public async Task DownloadFile(string url, string targetFile, Action<int> progress, string authorization = null, string accept = null, double timeout = 30, CancellationToken cancelToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(cancelToken, timeoutCts.Token);

        try
        {
            using var request = CreateRequest(HttpMethod.Get, url, authorization, accept);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            response.EnsureSuccessStatusCode();

            var totalBytes        = response.Content.Headers.ContentLength ?? -1L;
            var canReportProgress = totalBytes != -1;

            using var contentStream = await response.Content.ReadAsStreamAsync(linkedCts.Token);

            // 确保目录存在
            var dir = Path.GetDirectoryName(targetFile);

            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var fileStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var  buffer    = new byte[8192];
            long totalRead = 0;
            int  bytesRead;
            var  lastProgress = -1;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, linkedCts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, linkedCts.Token);

                if (canReportProgress)
                {
                    totalRead += bytesRead;
                    var currentProgress = (int)(totalRead * 100 / totalBytes);

                    if (currentProgress > lastProgress)
                    {
                        lastProgress = currentProgress;
                        progress(currentProgress);
                    }
                }
            }

            if (canReportProgress && lastProgress != 100)
                progress(100);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download file from {Url} to {TargetFile}", url, targetFile);

            if (File.Exists(targetFile))
            {
                try
                {
                    File.Delete(targetFile);
                }
                catch
                {
                    /* 忽略删除失败 */
                }
            }

            throw; // 重新抛出异常供上层处理
        }
    }

    public async Task<byte[]> DownloadBytes(string url, string authorization = null, string accept = null, double timeout = 30)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));

        try
        {
            using var request  = CreateRequest(HttpMethod.Get, url, authorization, accept);
            using var response = await httpClient.SendAsync(request, cts.Token);

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download bytes from {Url}", url);
            throw;
        }
    }

    public async Task<string> DownloadString(string url, string authorization = null, string accept = null, double timeout = 30)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));

        try
        {
            using var request  = CreateRequest(HttpMethod.Get, url, authorization, accept);
            using var response = await httpClient.SendAsync(request, cts.Token);

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download string from {Url}", url);
            throw;
        }
    }

    // 辅助方法：构建 HttpRequestMessage
    private HttpRequestMessage CreateRequest(HttpMethod method, string url, string authorization, string accept)
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
                Log.Warning("Invalid authorization header format: {Auth}", authorization);
            }
        }

        return request;
    }
}
