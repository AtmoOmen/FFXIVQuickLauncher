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
        Version?             defaultRequestVersion = null,
        HttpVersionPolicy    defaultVersionPolicy  = HttpVersionPolicy.RequestVersionOrHigher
    )
    {
        var client = new HttpClient
        (
            new SocketsHttpHandler
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
            }
        );

        client.DefaultRequestVersion = defaultRequestVersion ?? HttpVersion.Version20;
        client.DefaultVersionPolicy  = defaultVersionPolicy;
        return client;
    }
}
