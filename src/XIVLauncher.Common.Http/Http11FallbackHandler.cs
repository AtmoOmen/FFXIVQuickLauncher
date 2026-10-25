using System.Collections.Concurrent;
using System.Net;

namespace XIVLauncher.Common.Http;

public sealed class Http11FallbackHandler
(
    HttpMessageHandler innerHandler
) : DelegatingHandler(innerHandler)
{
    private readonly ConcurrentDictionary<string, long> http11Authorities = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync
    (
        HttpRequestMessage request,
        CancellationToken  cancellationToken
    )
    {
        var authority = request.RequestUri!.GetLeftPart(UriPartial.Authority);

        if (http11Authorities.TryGetValue(authority, out var retryAfter) && Environment.TickCount64 < retryAfter)
        {
            ForceHttp11(request);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when
        (
            request.Version.Major >= 2                                                                            &&
            (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)                               &&
            ex.HttpRequestError is HttpRequestError.HttpProtocolError or HttpRequestError.VersionNegotiationError &&
            !cancellationToken.IsCancellationRequested
        )
        {
            http11Authorities[authority] = Environment.TickCount64 + (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
        }

        using var retryRequest = await CloneHttpRequestMessageAsync(request, cancellationToken).ConfigureAwait(false);
        ForceHttp11(retryRequest);
        return await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
    }

    private static void ForceHttp11
    (
        HttpRequestMessage request
    )
    {
        request.Version       = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
    }

    private static async Task<HttpRequestMessage> CloneHttpRequestMessageAsync
    (
        HttpRequestMessage request,
        CancellationToken  cancellationToken
    )
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content == null)
            return clone;

        var ms = new MemoryStream();
        await request.Content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        ms.Position   = 0;
        clone.Content = new StreamContent(ms);

        foreach (var header in request.Content.Headers)
            clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return clone;
    }
}
