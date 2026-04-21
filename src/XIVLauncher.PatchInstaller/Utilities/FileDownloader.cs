using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Game.Exceptions;

namespace XIVLauncher.PatchInstaller.Utilities;

public class FileDownloader
{
    public long BytesPerSecond => speedAccumulator.Sum() * 1000 / (SpeedBucketCount * SpeedBucketDuration);

    public        long TotalLength      { get; private set; } = -1;
    public        long DownloadedLength { get; private set; }
    private const int  BufferSize      = 65536;
    private const int  ConnectionDelay = 200;

    private const    int    SpeedBucketCount    = 50;
    private const    int    SpeedBucketDuration = 100;
    private readonly long[] speedBucketBaseTick = new long[SpeedBucketCount];
    private readonly long[] speedAccumulator    = new long[SpeedBucketCount];

    private readonly HttpClient                        client;
    private readonly string                            localPath;
    private readonly string?                           sid;
    private readonly CancellationToken                 cancellationToken;
    private readonly int                               numThreads;
    private readonly List<Fragment>                    fragments = new();
    private readonly Channel<Tuple<long, int, byte[]>> fileChannel;
    private          string                            url;

    private Stream? localFile;

    public FileDownloader(HttpClient client, string url, string localPath, string? sid, CancellationToken cancellationToken, int numThreads)
    {
        this.client            = client;
        this.url               = url;
        this.localPath         = localPath;
        this.sid               = sid;
        this.cancellationToken = cancellationToken;
        this.numThreads        = numThreads;
        fileChannel            = Channel.CreateBounded<Tuple<long, int, byte[]>>(numThreads * 16);
    }

    public async Task Download()
    {
        var tempPath = $"{localPath}.tmp.{Process.GetCurrentProcess().Id:X08}{Environment.TickCount:X16}0000";
        for (var i = 1; File.Exists(tempPath); i++)
            tempPath = $"{localPath}.tmp.{Process.GetCurrentProcess().Id:X08}{Environment.TickCount:X16}{i:X04}";

        localFile = File.Create(tempPath);
        var flushTask = FlushTask();

        try
        {
            try
            {
                HttpResponseMessage? response = null;
                Stream?              stream   = null;

                while (true)
                {
                    try
                    {
                        response = await GetResponseAsync(0, 0);
                        stream   = await response.Content.ReadAsStreamAsync();

                        switch (response.StatusCode)
                        {
                            case HttpStatusCode.OK:
                                break;

                            case HttpStatusCode.Redirect:
                            case HttpStatusCode.TemporaryRedirect:
                                url = response.Headers.Location.ToString();
                                continue;

                            default:
                                throw new InvalidResponseException("Invalid response status code", response.StatusCode.ToString());
                        }

                        if (response.Content.Headers.ContentLength is not { } length)
                        {
                            Log.Information("File size unknown");

                            await stream.CopyToAsync(localFile);
                            localFile?.Dispose();
                            localFile = null;
                            File.Move(tempPath, localPath);
                            return;
                        }

                        TotalLength = length;
                        Log.Information("Downloading {length:##,###} bytes", length);
                        fragments.Add(new(this, response, stream, 0, length));
                        fragments.Add(new(this, null, null, length, length));
                        response = null;
                        stream   = null;

                        break;
                    }
                    finally
                    {
                        response?.Dispose();
                        stream?.Dispose();
                        response = null;
                        stream   = null;
                    }
                }

                var working = new List<Task>();

                while (await MergeAndFindGap() != -1)
                {
                    working.Clear();
                    working.AddRange(fragments.Select(x => x.DownloadTask).Where(x => !x.IsCompleted));

                    if (working.Count >= numThreads)
                    {
                        await Task.WhenAny(working.Append(Task.Delay(200, cancellationToken)));
                        _ = await MergeAndFindGap();
                        continue;
                    }

                    await Task.Delay(ConnectionDelay, cancellationToken);
                    var largestGap = await MergeAndFindGap();
                    if (largestGap == -1)
                        break;

                    var cur  = fragments[largestGap];
                    var next = fragments[largestGap + 1];

                    var fragStart = cur.DownloadTask.IsCompleted ? cur.AvailEnd : (cur.AvailEnd + next.Start) / 2;
                    var fragEnd   = next.Start;
                    if (fragStart >= fragEnd)
                        continue;

                    try
                    {
                        response = await GetResponseAsync(fragStart, fragEnd);
                        stream   = await response.Content.ReadAsStreamAsync();

                        if (response.StatusCode != HttpStatusCode.PartialContent)
                            throw new InvalidResponseException($"Invalid response status code: {response.StatusCode}", "");

                        fragments[largestGap].MaxEnd = fragStart;
                        fragments.Insert(largestGap + 1, new(this, response, stream, fragStart, fragEnd));
                        response = null;
                        stream   = null;
                    }
                    finally
                    {
                        response?.Dispose();
                        stream?.Dispose();
                        response = null;
                        stream   = null;
                    }
                }
            }
            finally
            {
                foreach (var f in fragments)
                    await f.DisposeAsync();

                fileChannel.Writer.Complete();
                await flushTask;
            }

            localFile?.Dispose();
            localFile = null;
            File.Move(tempPath, localPath);
        }
        catch (Exception)
        {
            localFile?.Dispose();
            localFile = null;

            try
            {
                File.Delete(tempPath);
            }
            catch (Exception)
            {
                // ignore
            }

            throw;
        }
        finally
        {
            foreach (var f in fragments)
                await f.DisposeAsync();
            fragments.Clear();
        }
    }

    private async Task<HttpResponseMessage> GetResponseAsync(long start, long end)
    {
        if (start == 0 && end == 0)
            Log.Verbose("Connecting: {url}", url);
        else
            Log.Verbose("Connecting: {url} ({start:##,###}-{end:##,###})", url, start, end);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("User-Agent", Constants.PatcherUserAgent);
        req.Headers.Add("Connection", "Keep-Alive");
        if (start != 0 || end != 0)
            req.Headers.Range = new(start == 0 ? null : start, end == 0 ? null : end);
        if (sid != null)
            req.Headers.Add("X-Patch-Unique-Id", sid);
        // Note: "req" has to be alive during the await, so we async+return await instead of plain return.
        return await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task FlushTask()
    {
        try
        {
            while (true)
            {
                var (from, size, buffer) = await fileChannel.Reader.ReadAsync(cancellationToken);
                localFile!.Seek(from, SeekOrigin.Begin);
                await localFile.WriteAsync(buffer, 0, size, cancellationToken);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (ChannelClosedException)
        {
            // ignore
        }
    }

    private async Task<int> MergeAndFindGap()
    {
        var largestGap = -1;

        for (var i = 0; i < fragments.Count - 1; i++)
        {
            var cur  = fragments[i];
            var next = fragments[i + 1];

            // both are finished and continuous then merge
            if (cur.AvailEnd >= next.Start)
            {
                if (cur.DownloadTask.IsCompleted && next.DownloadTask.IsCompleted)
                {
                    fragments[i] = new(this, null, null, cur.Start, next.AvailEnd);
                    fragments.RemoveAt(i + 1);
                    await cur.DisposeAsync();
                    await next.DisposeAsync();
                    i--;
                    continue;
                }

                await cur.DisposeAsync();
            }

            if (largestGap == -1)
                largestGap = i;
            else
            {
                var prevGap = fragments[largestGap + 1].Start - fragments[largestGap].AvailEnd;
                var currGap = next.Start                      - cur.AvailEnd;
                if ((cur.DownloadTask.IsCompleted ? currGap : currGap / 2) > prevGap)
                    largestGap = i;
            }
        }

        DownloadedLength = Math.Min
        (
            fragments.Sum(x => x.AvailEnd - x.Start),
            TotalLength == 0 ? long.MaxValue : TotalLength
        );

        return largestGap;
    }

    private async Task Write(long from, int size, byte[] buffer)
    {
        var baseTick    = Environment.TickCount / SpeedBucketDuration;
        var speedBucket = baseTick              % SpeedBucketCount;

        if (speedBucketBaseTick[speedBucket] != baseTick)
        {
            speedBucketBaseTick[speedBucket] = baseTick;
            speedAccumulator[speedBucket]    = size;
        }
        else
            speedAccumulator[speedBucket] += size;

        await fileChannel.Writer.WriteAsync(Tuple.Create(from, size, buffer), cancellationToken);
    }

    private sealed class Fragment : IDisposable, IAsyncDisposable
    {
        public readonly long Start;
        public readonly Task DownloadTask;

        public long MaxEnd;
        public long AvailEnd;

        private readonly HttpResponseMessage?     httpResponseMessage;
        private readonly Stream?                  stream;
        private readonly FileDownloader           parent;
        private          CancellationTokenSource? cancellationTokenSource;

        public Fragment(FileDownloader parent, HttpResponseMessage? httpResponseMessage, Stream? stream, long start, long maxEnd)
        {
            this.parent              = parent;
            this.httpResponseMessage = httpResponseMessage;
            this.stream              = stream;
            cancellationTokenSource  = stream is null ? null : new();

            Start        = start;
            MaxEnd       = maxEnd;
            AvailEnd     = stream is null ? maxEnd : start;
            DownloadTask = stream is null ? Task.CompletedTask : Task.Run(TaskBody);
        }

        #region Disposal

        public void Dispose()
        {
            cancellationTokenSource?.Cancel();
            DownloadTask.Wait();
            cancellationTokenSource?.Dispose();
            cancellationTokenSource = null;
        }

        #endregion

        public async ValueTask DisposeAsync()
        {
            cancellationTokenSource?.Cancel();

            try
            {
                await DownloadTask;
            }
            catch (Exception)
            {
                // ignore
            }

            cancellationTokenSource?.Dispose();
            cancellationTokenSource = null;
        }

        private async Task TaskBody()
        {
            using var _1    = httpResponseMessage!;
            using var _2    = stream!;
            var       token = cancellationTokenSource!.Token;

            while (true)
            {
                var avail = unchecked((int)Math.Min(BufferSize, MaxEnd - AvailEnd));
                if (avail <= 0)
                    break;

                var buf = ArrayPool<byte>.Shared.Rent(avail);
                avail = await stream!.ReadAsync(buf, 0, avail, token);

                if (avail == 0)
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    break;
                }

                await parent.Write(AvailEnd, avail, buf);
                AvailEnd += avail;
            }
        }
    }
}
