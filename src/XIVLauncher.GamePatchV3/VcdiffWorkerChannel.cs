using System.ComponentModel;
using System.Diagnostics;
using Serilog;
using SharedMemory;

namespace XIVLauncher.GamePatchV3;

internal sealed class VcdiffWorkerChannel : IDisposable
{
    private readonly string  workerExecutablePath;
    private readonly string? dotnetRootPath;
    private readonly bool    asAdmin;
    private          Process?   workerProcess;
    private          RpcBuffer? rpcBuffer;
    private          bool       isDisposed;

    internal VcdiffWorkerChannel
    (
        string  workerExecutablePath,
        string? dotnetRootPath,
        bool    asAdmin
    )
    {
        this.workerExecutablePath = workerExecutablePath;
        this.dotnetRootPath       = dotnetRootPath;
        this.asAdmin              = asAdmin;
    }

    internal Process? WorkerProcess =>
        workerProcess;

    internal RpcBuffer EnsureStarted()
    {
        if (rpcBuffer != null && workerProcess is { HasExited: false })
            return rpcBuffer;

        StopWorker();

        var channelName = "VcdiffShim" + Guid.NewGuid();
        var buffer      = new RpcBuffer(channelName, (_, _) => { });

        Log.Information("[VcdiffClient] 正在启动 V3 差分进程, 路径 {WorkerExecutablePath}, 提权 {AsAdmin}, 通道 {ChannelName}", workerExecutablePath, asAdmin, channelName);

        var process = new Process
        {
            StartInfo = CreateProcessStartInfo(workerExecutablePath, $"{Environment.ProcessId} {channelName}")
        };
#if !DEBUG
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.WindowStyle    = ProcessWindowStyle.Hidden;
#endif
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            buffer.Dispose();
            process.Dispose();

            if (ex is Win32Exception { HResult: 1223 })
                throw new OperationCanceledException();

            throw;
        }

        rpcBuffer     = buffer;
        workerProcess = process;

        Log.Information("[VcdiffClient] V3 差分进程已启动, PID {ProcessId}", process.Id);
        return buffer;
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        isDisposed = true;
        StopWorker();
    }

    private void StopWorker()
    {
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
}
