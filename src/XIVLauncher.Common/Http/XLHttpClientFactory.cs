using System;
using System.Net;
using System.Net.Http;

namespace XIVLauncher.Common.Http;

public static class XLHttpClientFactory
{
    public static HttpClient Create
    (
        TimeSpan             connectTimeout,
        int                  maxConnectionsPerServer,
        DecompressionMethods automaticDecompression,
        bool                 useProxy = true
    )
    {
        var client = new HttpClient
        (
            new SocketsHttpHandler
            {
                UseProxy                       = useProxy,
                ConnectTimeout                 = connectTimeout,
                MaxConnectionsPerServer        = maxConnectionsPerServer,
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime       = TimeSpan.FromMinutes(3),
                PooledConnectionIdleTimeout    = TimeSpan.FromMinutes(1),
                Expect100ContinueTimeout       = TimeSpan.Zero,
                ResponseDrainTimeout           = TimeSpan.FromSeconds(2),
                AutomaticDecompression         = automaticDecompression,
                ConnectCallback                = HappyEyeballsCallback.ConnectCallback
            }
        );

        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionOrHigher;
        return client;
    }
}
