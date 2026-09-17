using System.Collections.Concurrent;
using System.Text;
using Serilog;

namespace XIVLauncher.GamePatchV3;

public sealed class VcdiffClient : IDisposable
{
    private readonly string                               workerExecutablePath;
    private readonly string?                              dotnetRootPath;
    private readonly bool                                 asAdmin;
    private readonly SemaphoreSlim                        channelGate;
    private readonly ConcurrentQueue<VcdiffWorkerChannel> idleChannels = new();
    private readonly ConcurrentBag<VcdiffWorkerChannel>   allChannels  = [];
    private          bool                                 isDisposed;

    public VcdiffClient
    (
        string  workerExecutablePath,
        string? dotnetRootPath = null,
        bool    asAdmin        = false
    )
    {
        this.workerExecutablePath = workerExecutablePath;
        this.dotnetRootPath       = dotnetRootPath;
        this.asAdmin              = asAdmin;

        var channelCount = Math.Clamp(Environment.ProcessorCount, 1, MAX_CONCURRENT_MERGES);

        channelGate = new(channelCount, channelCount);
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        isDisposed = true;

        foreach (var channel in allChannels)
            channel.Dispose();

        channelGate.Dispose();
    }

    public async Task ApplyVcdiff
    (
        string                                  sourceFile,
        ReadOnlyMemory<byte>                    deltaData,
        string                                  targetFile,
        string                                  expectedMd5,
        long                                    expectedSize,
        IProgress<(long Progress, long Total)>? progress          = null,
        CancellationToken                       cancellationToken = default
    )
    {
        var requestData = BuildRequestData(sourceFile, targetFile, expectedMd5, expectedSize, deltaData.Span);
        await ApplyVcdiffRequest(sourceFile, deltaData.Length, targetFile, expectedSize, requestData, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyVcdiffRequest
    (
        string                                  sourceFile,
        int                                     deltaLength,
        string                                  targetFile,
        long                                    expectedSize,
        byte[]                                  requestData,
        IProgress<(long Progress, long Total)>? progress,
        CancellationToken                       cancellationToken
    )
    {
        Log.Information
            ("[VcdiffClient] 请求 V3 差分合并, 源 {SourceFile}, 差分大小 {DeltaSize}, 目标 {TargetFile}, 期望大小 {ExpectedSize}", sourceFile, deltaLength, targetFile, expectedSize);

        var channel  = await AcquireChannelAsync(cancellationToken).ConfigureAwait(false);
        var tempPath = string.Concat(targetFile, TEMP_EXTENSION);

        try
        {
            var rpcBuffer  = channel.EnsureStarted();
            var resultTask = rpcBuffer.RemoteRequestAsync(requestData, REQUEST_TIMEOUT_MS, cancellationToken);

            while (await Task.WhenAny(resultTask, Task.Delay(MERGE_POLL_INTERVAL_MS, cancellationToken)).ConfigureAwait(false) != resultTask)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (channel.WorkerProcess is { HasExited: true } exitedWorkerProcess)
                    throw new IOException($"V3 差分进程已退出，退出码 {exitedWorkerProcess.ExitCode}");

                try
                {
                    var current = File.Exists(tempPath) ?
                                      new FileInfo(tempPath).Length :
                                      0;
                    var total = expectedSize > 0 ? expectedSize : current > 0 ? Math.Max(current, 1) : 0;
                    progress?.Report((current, total));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Debug(ex, "[VcdiffClient] 无法读取差分临时文件进度 {Path}", tempPath);
                }
            }

            var response = await resultTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!response.Success)
            {
                if (channel.WorkerProcess is { HasExited: true })
                    throw new IOException($"V3 差分进程在响应前退出，退出码 {channel.WorkerProcess.ExitCode}");

                throw new TimeoutException("V3 差分进程未在预期时间内返回响应");
            }

            if (response.Data is null || response.Data.Length < sizeof(int))
                throw new IOException("V3 差分进程返回了空响应");

            using var reader = new BinaryReader(new MemoryStream(response.Data));
            var       result = reader.ReadInt32();

            if (result == RESULT_ERROR)
                throw new IOException($"V3 差分合并失败: {reader.ReadString()}");

            if (result != RESULT_PASS)
                throw new InvalidOperationException("未知的 V3 差分结果码");

            if (progress != null)
            {
                var completedSize = File.Exists(targetFile) ?
                                        new FileInfo(targetFile).Length :
                                        expectedSize;
                progress.Report((completedSize, completedSize));
            }

            Log.Information("[VcdiffClient] V3 差分合并完成 {TargetFile}", targetFile);
        }
        finally
        {
            ReleaseChannel(channel);
        }
    }

    private async Task<VcdiffWorkerChannel> AcquireChannelAsync
    (
        CancellationToken cancellationToken
    )
    {
        await channelGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (idleChannels.TryDequeue(out var channel))
            return channel;

        channel = new VcdiffWorkerChannel(workerExecutablePath, dotnetRootPath, asAdmin);
        allChannels.Add(channel);
        return channel;
    }

    private void ReleaseChannel
    (
        VcdiffWorkerChannel channel
    )
    {
        if (channel.WorkerProcess is { HasExited: false })
            idleChannels.Enqueue(channel);
        else
            channel.Dispose();

        channelGate.Release();
    }

    internal static byte[] BuildRequestData
    (
        string             sourceFile,
        string             targetFile,
        string             expectedMd5,
        long               expectedSize,
        ReadOnlySpan<byte> deltaData
    )
    {
        var requestData = CreateRequestData(sourceFile, targetFile, expectedMd5, expectedSize, deltaData.Length, out var deltaOffset);
        deltaData.CopyTo(requestData.AsSpan(deltaOffset));
        return requestData;
    }

    private static byte[] CreateRequestData
    (
        string  sourceFile,
        string  targetFile,
        string  expectedMd5,
        long    expectedSize,
        int     deltaLength,
        out int deltaOffset
    )
    {
        var requestLength = checked
        (
            sizeof(int)                          +
            GetSerializedStringSize(sourceFile)  +
            GetSerializedStringSize(targetFile)  +
            GetSerializedStringSize(expectedMd5) +
            sizeof(long)                         +
            sizeof(int)                          +
            deltaLength
        );
        var       requestData   = GC.AllocateUninitializedArray<byte>(requestLength);
        using var requestStream = new MemoryStream(requestData, true);
        using var writer        = new BinaryWriter(requestStream, Encoding.UTF8, true);
        writer.Write(VCDIFF_OPCODE);
        writer.Write(sourceFile);
        writer.Write(targetFile);
        writer.Write(expectedMd5);
        writer.Write(expectedSize);
        writer.Write(deltaLength);
        deltaOffset = checked((int)requestStream.Position);
        return requestData;
    }

    private static int GetSerializedStringSize
    (
        string value
    )
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var prefixSize = byteCount switch
        {
            < 1 << 7  => 1,
            < 1 << 14 => 2,
            < 1 << 21 => 3,
            < 1 << 28 => 4,
            _         => 5
        };
        return checked(prefixSize + byteCount);
    }

    #region Constants

    private const int    VCDIFF_OPCODE          = 0;
    private const int    RESULT_PASS            = 0;
    private const int    RESULT_ERROR           = 2;
    private const int    MAX_CONCURRENT_MERGES  = 4;
    private const int    MERGE_POLL_INTERVAL_MS = 250;
    private const int    REQUEST_TIMEOUT_MS     = 864000000;
    private const string TEMP_EXTENSION         = ".tmp";

    #endregion
}
