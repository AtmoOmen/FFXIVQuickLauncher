using System.Net;

namespace XIVLauncher.Common.Http;

public static class XLHttpClientFactory
{
    public static HttpClient Create
    (
        TimeSpan             connectTimeout,
        int                  maxConnectionsPerServer,
        DecompressionMethods automaticDecompression
    )
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy                       = true,
            ConnectTimeout                 = connectTimeout,
            MaxConnectionsPerServer        = maxConnectionsPerServer,
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay             = TimeSpan.FromSeconds(KEEP_ALIVE_PING_DELAY_SECONDS),
            KeepAlivePingTimeout           = TimeSpan.FromSeconds(KEEP_ALIVE_PING_TIMEOUT_SECONDS),
            KeepAlivePingPolicy            = HttpKeepAlivePingPolicy.WithActiveRequests,
            PooledConnectionLifetime       = TimeSpan.FromMinutes(3),
            PooledConnectionIdleTimeout    = TimeSpan.FromMinutes(1),
            Expect100ContinueTimeout       = TimeSpan.Zero,
            ResponseDrainTimeout           = TimeSpan.FromSeconds(2),
            AutomaticDecompression         = automaticDecompression,
        };

        return new HttpClient(new Http11FallbackHandler(handler));
    }

    #region Constants

    private const int KEEP_ALIVE_PING_DELAY_SECONDS = 30;

    private const int KEEP_ALIVE_PING_TIMEOUT_SECONDS = 10;

    #endregion
}
