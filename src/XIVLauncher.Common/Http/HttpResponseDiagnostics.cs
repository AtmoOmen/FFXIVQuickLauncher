using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace XIVLauncher.Common.Http;

public static class HttpResponseDiagnostics
{
    public static async Task EnsureSuccessWithDiagnosticsAsync
    (
        this HttpResponseMessage response,
        CancellationToken cancellationToken = default
    )
    {
        if (response.IsSuccessStatusCode)
            return;

        throw await CreateFailureExceptionAsync(response, null, cancellationToken).ConfigureAwait(false);
    }

    public static HttpRequestException? FindHttpRequestException(this Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is HttpRequestException httpRequestException)
                return httpRequestException;

            if (current is AggregateException aggregateException)
            {
                foreach (var innerException in aggregateException.InnerExceptions)
                {
                    var found = innerException.FindHttpRequestException();
                    if (found != null)
                        return found;
                }
            }
        }

        return null;
    }

    public static async Task<HttpRequestException> CreateFailureExceptionAsync
    (
        HttpResponseMessage response,
        string?             prefix,
        CancellationToken   cancellationToken
    )
    {
        var message = $"HTTP 请求失败, 状态码 {(int)response.StatusCode} ({response.StatusCode})";
        if (!string.IsNullOrWhiteSpace(prefix))
            message = $"{prefix}{Environment.NewLine}{message}";
        var errorSource = response.Headers.TryGetValues(PROXY_ERROR_SOURCE_HEADER, out var errorSources)
            ? string.Join(", ", errorSources)
            : null;
        var cfRay = response.Headers.TryGetValues(CF_RAY_HEADER, out var cfRays)
            ? string.Join(", ", cfRays)
            : null;
        var proxyCache = response.Headers.TryGetValues(PROXY_CACHE_HEADER, out var proxyCaches)
            ? string.Join(", ", proxyCaches)
            : null;

        if (!string.IsNullOrWhiteSpace(errorSource))
            message += $"{Environment.NewLine}诊断来源: {errorSource}";

        if (!string.IsNullOrWhiteSpace(cfRay))
            message += $"{Environment.NewLine}Cloudflare Ray: {cfRay}";

        if (!string.IsNullOrWhiteSpace(proxyCache))
            message += $"{Environment.NewLine}代理缓存: {proxyCache}";

        if (response.Content != null)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            body = body.Replace("\r", "").Replace("\n", " ").Trim();

            if (body.Length > RESPONSE_BODY_LIMIT)
                body = body[..RESPONSE_BODY_LIMIT];

            if (!string.IsNullOrWhiteSpace(body))
                message += $"{Environment.NewLine}响应内容: {body}";
        }

        return new HttpRequestException(message, null, response.StatusCode);
    }

    #region Constants

    private const int RESPONSE_BODY_LIMIT = 800;

    private const string CF_RAY_HEADER = "cf-ray";

    private const string PROXY_CACHE_HEADER = "x-proxy-cache";

    private const string PROXY_ERROR_SOURCE_HEADER = "x-proxy-error-source";

    #endregion
}
