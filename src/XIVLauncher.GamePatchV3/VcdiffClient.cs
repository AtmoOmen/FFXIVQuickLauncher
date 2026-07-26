using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Serilog;
using SharedMemory;

namespace XIVLauncher.GamePatchV3;

public sealed class VcdiffClient
(
    string  workerExecutablePath,
    string? dotnetRootPath = null,
    bool    asAdmin        = false
) : IDisposable
{
    private Process?   workerProcess;
    private RpcBuffer? rpcBuffer;
    private bool       isDisposed;

    public void Dispose()
    {
        if (isDisposed)
            return;

        isDisposed = true;

        try
        {
            rpcBuffer?.RemoteRequest([], 100);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[VcdiffClient] 关闭 RPC 通道时远端未响应");
        }

        if (workerProcess is { HasExited: false })
        {
            workerProcess.WaitForExit(1000);

            try
            {
                workerProcess.Kill();
            }
            catch (Exception ex)
            {
                if (!workerProcess.HasExited)
                    throw;

                Log.Debug(ex, "[VcdiffClient] 差分进程已在终止期间退出");
            }
        }

        rpcBuffer?.Dispose();
        workerProcess?.Dispose();
        rpcBuffer     = null;
        workerProcess = null;
    }

    public async Task ApplyVcdiff
    (
        string                                  sourceFile,
        string                                  deltaFile,
        string                                  targetFile,
        string                                  expectedMd5,
        long                                    expectedSize,
        IProgress<(long Progress, long Total)>? progress          = null,
        CancellationToken                       cancellationToken = default
    )
    {
        await using var deltaStream = new FileStream
        (
            deltaFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        if (deltaStream.Length > int.MaxValue)
            throw new InvalidDataException("V3 差分数据过大");

        await ApplyVcdiff(sourceFile, deltaStream, (int)deltaStream.Length, targetFile, expectedMd5, expectedSize, null, progress, cancellationToken).ConfigureAwait
            (false);
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

    public async Task ApplyVcdiff
    (
        string                                  sourceFile,
        Stream                                  deltaStream,
        int                                     deltaLength,
        string                                  targetFile,
        string                                  expectedMd5,
        long                                    expectedSize,
        IProgress<(long Progress, long Total)>? extractionProgress,
        IProgress<(long Progress, long Total)>? mergeProgress,
        CancellationToken                       cancellationToken = default
    )
    {
        if (deltaLength < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaLength));

        var requestData = CreateRequestData(sourceFile, targetFile, expectedMd5, expectedSize, deltaLength, out var deltaOffset);
        var extracted   = 0;

        while (extracted < deltaLength)
        {
            var read = await deltaStream.ReadAsync(requestData.AsMemory(deltaOffset + extracted, deltaLength - extracted), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("V3 差分数据提前结束");

            extracted += read;
            extractionProgress?.Report((extracted, deltaLength));
        }

        await ApplyVcdiffRequest(sourceFile, deltaLength, targetFile, expectedSize, requestData, mergeProgress, cancellationToken).ConfigureAwait(false);
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

        EnsureWorkerStarted();

        var resultTask = rpcBuffer!.RemoteRequestAsync(requestData, 864000000, cancellationToken);
        var tempPath   = string.Concat(targetFile, ".tmp");

        while (await Task.WhenAny(resultTask, Task.Delay(250, cancellationToken)).ConfigureAwait(false) != resultTask)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (workerProcess is { HasExited: true } exitedWorkerProcess)
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
            if (workerProcess is { HasExited: true })
                throw new IOException($"V3 差分进程在响应前退出，退出码 {workerProcess.ExitCode}");
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

    private void EnsureWorkerStarted()
    {
        if (workerProcess is { HasExited: false })
            return;

        if (rpcBuffer != null)
        {
            rpcBuffer.Dispose();
            rpcBuffer = null;
        }

        if (workerProcess != null)
        {
            workerProcess.Dispose();
            workerProcess = null;
        }

        var channelName = "VcdiffShim" + Guid.NewGuid();
        rpcBuffer = new(channelName, (_, _) => { });

        Log.Information("[VcdiffClient] 正在启动 V3 差分进程, 路径 {WorkerExecutablePath}, 提权 {AsAdmin}, 通道 {ChannelName}", workerExecutablePath, asAdmin, channelName);

        workerProcess = new()
        {
            StartInfo = CreateProcessStartInfo(workerExecutablePath, $"{Environment.ProcessId} {channelName}")
        };
#if !DEBUG
        workerProcess.StartInfo.CreateNoWindow = true;
        workerProcess.StartInfo.WindowStyle    = ProcessWindowStyle.Hidden;
#endif
        try
        {
            workerProcess.Start();
        }
        catch (Win32Exception ex) when (ex.HResult == 1223)
        {
            throw new OperationCanceledException();
        }

        Log.Information("[VcdiffClient] V3 差分进程已启动, PID {ProcessId}", workerProcess.Id);
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

    private ProcessStartInfo CreateProcessStartInfo
    (
        string executablePath,
        string arguments
    )
    {
        var workingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;

        var startInfo = new ProcessStartInfo(executablePath)
        {
            Arguments        = arguments,
            UseShellExecute  = asAdmin,
            WorkingDirectory = workingDirectory
        };

        if (asAdmin)
        {
            startInfo.Verb = "runas";

            if (!string.IsNullOrWhiteSpace(dotnetRootPath))
                Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnetRootPath);

            return startInfo;
        }

        if (!string.IsNullOrWhiteSpace(dotnetRootPath))
        {
            startInfo.Environment["DOTNET_ROOT"]              = dotnetRootPath;
            startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        }

        return startInfo;
    }

    #region Constants

    private const int VCDIFF_OPCODE = 0;
    private const int RESULT_PASS   = 0;
    private const int RESULT_ERROR  = 2;

    #endregion
}
