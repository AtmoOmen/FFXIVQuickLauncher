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
            PooledConnectionLifetime       = TimeSpan.FromMinutes(3),
            PooledConnectionIdleTimeout    = TimeSpan.FromMinutes(1),
            Expect100ContinueTimeout       = TimeSpan.Zero,
            ResponseDrainTimeout           = TimeSpan.FromSeconds(2),
            AutomaticDecompression         = automaticDecompression,
            ConnectCallback                = HappyEyeballsCallback.ConnectCallback
        };

        return new HttpClient(new Http11FallbackHandler(handler));
    }
}
