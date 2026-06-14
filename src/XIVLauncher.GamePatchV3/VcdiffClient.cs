using System.ComponentModel;
using System.Diagnostics;
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
        catch (Exception)
        {
        }

        if (workerProcess is { HasExited: false })
        {
            workerProcess.WaitForExit(1000);

            try
            {
                workerProcess.Kill();
            }
            catch (Exception)
            {
                if (!workerProcess.HasExited)
                    throw;
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
        var deltaData = await File.ReadAllBytesAsync(deltaFile, cancellationToken).ConfigureAwait(false);
        await ApplyVcdiff(sourceFile, deltaData, targetFile, expectedMd5, expectedSize, progress, cancellationToken).ConfigureAwait(false);
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
        if (deltaData.Length > int.MaxValue)
            throw new InvalidDataException("V3 差分数据过大");

        Log.Information("[VcdiffClient] 请求 V3 差分合并, 源 {SourceFile}, 差分大小 {DeltaSize}, 目标 {TargetFile}, 期望大小 {ExpectedSize}", sourceFile, deltaData.Length, targetFile, expectedSize);

        EnsureWorkerStarted();

        var writer = new BinaryWriter(new MemoryStream());
        writer.Write(VCDIFF_OPCODE);
        writer.Write(sourceFile);
        writer.Write(targetFile);
        writer.Write(expectedMd5);
        writer.Write(expectedSize);
        writer.Write(deltaData.Length);
        writer.Write(deltaData.Span);

        var requestData = ((MemoryStream)writer.BaseStream).ToArray();
        var resultTask  = rpcBuffer!.RemoteRequestAsync(requestData, 864000000, cancellationToken);
        var tempPath    = string.Concat(targetFile, ".tmp");

        while (await Task.WhenAny(resultTask, Task.Delay(250, cancellationToken)).ConfigureAwait(false) != resultTask)
        {
            if (workerProcess is { HasExited: true } exitedWorkerProcess)
                throw new IOException($"V3 差分进程已退出，退出码 {exitedWorkerProcess.ExitCode}");

            try
            {
                var current = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
                var total   = expectedSize > 0 ? expectedSize : current > 0 ? Math.Max(current, 1) : 0;
                progress?.Report((current, total));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
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
            var completedSize = File.Exists(targetFile) ? new FileInfo(targetFile).Length : expectedSize;
            progress.Report((completedSize, completedSize));
        }

        Log.Information("[VcdiffClient] V3 差分合并完成 {TargetFile}", targetFile);
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

    private ProcessStartInfo CreateProcessStartInfo(string executablePath, string arguments)
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
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnetRootPath);
            }

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
