using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;
using XIVLauncher.Common.Http;
using XIVLauncher.Common.Util;
using Xunit;

namespace XIVLauncher.Test.Network;

public sealed class UpdateDownloadTests : IDisposable
{
    private readonly string destination = Path.GetTempFileName();

    [Fact]
    public async Task ServerIgnoringRange_DownloadsFirstResponseWithoutProbe()
    {
        var payload = new byte[256 * 1024];
        RandomNumberGenerator.Fill(payload);
        var requests = 0;
        using var client = new HttpClient
        (
            new RequestHandler
            ((request, _) =>
                {
                    Interlocked.Increment(ref requests);
                    Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).Value);
                    return Task.FromResult
                    (
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(payload)
                        }
                    );
                }
            )
        );
        using var downloader = new HttpClientDownloadWithProgress([("https://updates.test/package", destination)], client);
        await downloader.Download();
        Assert.Equal(1,       requests);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task InterruptedRange_ResumesAtLastWrittenByteAndPreservesContent()
    {
        const int SEGMENT_LENGTH  = 2  << 20;
        const int RECEIVED_LENGTH = 64 << 10;
        var       payload         = new byte[(5 * SEGMENT_LENGTH) + 37];
        RandomNumberGenerator.Fill(payload);
        var     starts         = new ConcurrentBag<long>();
        var     interrupted    = 0;
        long    lastDownloaded = 0;
        double? lastPercentage = null;

        using var client = new HttpClient
        (
            new RequestHandler
            ((request, _) =>
                {
                    var range = Assert.Single(request.Headers.Range!.Ranges);
                    var start = range.From!.Value;
                    var end   = Math.Min(range.To!.Value, payload.Length - 1);
                    starts.Add(start);
                    if (start > 0)
                        Assert.Equal("\"version-1\"", request.Headers.IfRange!.EntityTag!.Tag);

                    var         bytes = payload.AsSpan((int)start, (int)(end - start + 1)).ToArray();
                    HttpContent content;
                    if (start == SEGMENT_LENGTH && Interlocked.Exchange(ref interrupted, 1) == 0)
                        content = new StreamContent(new InterruptedStream(bytes, RECEIVED_LENGTH));
                    else
                        content = new ByteArrayContent(bytes);

                    content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, payload.Length);
                    return Task.FromResult
                    (
                        new HttpResponseMessage(HttpStatusCode.PartialContent)
                        {
                            Headers = { ETag = new EntityTagHeaderValue("\"version-1\"") },
                            Content = content
                        }
                    );
                }
            )
        );
        using var downloader = new HttpClientDownloadWithProgress([("https://updates.test/package", destination)], client);
        downloader.ProgressChanged += (_, downloaded, percentage) =>
        {
            lastDownloaded = downloaded;
            lastPercentage = percentage;
        };
        await downloader.Download();

        Assert.Contains(SEGMENT_LENGTH + RECEIVED_LENGTH, starts);
        Assert.Equal(payload.Length,           lastDownloaded);
        Assert.Equal(100d,                     lastPercentage);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(destination)));
    }

    [Fact]
    public async Task MissingFile_DoesNotRetryPermanentStatus()
    {
        var requests = 0;
        using var client = new HttpClient
        (
            new RequestHandler
            ((_, _) =>
                {
                    Interlocked.Increment(ref requests);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }
            )
        );
        using var downloader = new HttpClientDownloadWithProgress([("https://updates.test/missing", destination)], client);
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.Download());
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task WrongContentRange_IsRejectedBeforeWriting()
    {
        var requests = 0;
        using var client = new HttpClient
        (
            new RequestHandler
            ((_, _) =>
                {
                    Interlocked.Increment(ref requests);
                    var content = new ByteArrayContent([1, 2, 3]);
                    content.Headers.ContentRange = new ContentRangeHeaderValue(1, 3, 4);
                    return Task.FromResult
                    (
                        new HttpResponseMessage(HttpStatusCode.PartialContent)
                        {
                            Content = content
                        }
                    );
                }
            )
        );
        using var downloader = new HttpClientDownloadWithProgress([("https://updates.test/package", destination)], client);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.Download());
        Assert.Equal(1, requests);
        Assert.Equal(0, new FileInfo(destination).Length);
    }

    [Fact]
    public async Task CancelledRequest_StopsWithoutRetry()
    {
        var       requests     = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var client = new HttpClient
        (
            new RequestHandler
            (async (_, token) =>
                {
                    Interlocked.Increment(ref requests);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
            )
        );
        using var downloader = new HttpClientDownloadWithProgress([("https://updates.test/package", destination)], client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.Download(cancellation.Token));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task IncompleteBody_IsRetriedWithoutCountingDiscardedBytes()
    {
        var  payload        = new byte[1024];
        var  requests       = 0;
        long lastDownloaded = 0;
        using var client = new HttpClient
        (
            new RequestHandler
            ((_, _) =>
                {
                    var body = Interlocked.Increment(ref requests) == 1 ?
                                   payload[..512] :
                                   payload;
                    var content = new ByteArrayContent(body);
                    content.Headers.ContentLength = payload.Length;
                    return Task.FromResult
                    (
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = content
                        }
                    );
                }
            )
        );
        using var downloader = new HttpClientDownloadWithProgress([("https://updates.test/package", destination)], client);
        downloader.ProgressChanged += (_, downloaded, _) => lastDownloaded = downloaded;
        await downloader.Download();
        Assert.Equal(2,              requests);
        Assert.Equal(payload.Length, lastDownloaded);
        Assert.Equal(payload,        await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task ProtocolFailure_OnlyChangesVersionForAffectedAuthority()
    {
        var versions = new ConcurrentBag<(string Host, int Version)>();
        var failure  = 0;
        using var handler = new Http11FallbackHandler
        (
            new RequestHandler
            ((request, _) =>
                {
                    versions.Add((request.RequestUri!.Host, request.Version.Major));
                    if (request.RequestUri.Host == "broken.test" && Interlocked.Exchange(ref failure, 1) == 0)
                        throw new HttpRequestException(HttpRequestError.HttpProtocolError, "模拟 HTTP/2 协议错误");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }
            )
        );
        using var client       = new HttpMessageInvoker(handler);
        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, "https://broken.test/version");
        firstRequest.Version = HttpVersion.Version20;
        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, "https://healthy.test/version");
        secondRequest.Version = HttpVersion.Version20;
        using var thirdRequest = new HttpRequestMessage(HttpMethod.Get, "https://broken.test/assets");
        thirdRequest.Version = HttpVersion.Version20;
        using var firstResponse  = await client.SendAsync(firstRequest,  CancellationToken.None);
        using var secondResponse = await client.SendAsync(secondRequest, CancellationToken.None);
        using var thirdResponse  = await client.SendAsync(thirdRequest,  CancellationToken.None);

        Assert.Single(versions, item => item == ("broken.test", 2));
        Assert.Equal(2, versions.Count(item => item == ("broken.test", 1)));
        Assert.Single(versions, item => item == ("healthy.test", 2));
    }

    [Fact]
    public async Task ConnectionFailure_DoesNotDisableHttp2()
    {
        var requests = 0;
        using var handler = new Http11FallbackHandler
        (
            new RequestHandler
            ((_, _) =>
                {
                    Interlocked.Increment(ref requests);
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "模拟连接失败");
                }
            )
        );
        using var client  = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://updates.test/version");
        request.Version = HttpVersion.Version20;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, CancellationToken.None));
        Assert.Equal(1, requests);
    }

    public void Dispose() =>
        FileSystem.DeleteFile(destination, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

    private sealed class RequestHandler
    (
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync
        (
            HttpRequestMessage request,
            CancellationToken  cancellationToken
        ) =>
            send(request, cancellationToken);
    }

    private sealed class InterruptedStream
    (
        byte[] bytes,
        int    available
    ) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync
        (
            Memory<byte>      buffer,
            CancellationToken cancellationToken = default
        ) =>
            Position >= available ?
                throw new IOException("模拟连接中断") :
                base.ReadAsync(buffer[..Math.Min(buffer.Length, available - (int)Position)], cancellationToken);
    }
}
