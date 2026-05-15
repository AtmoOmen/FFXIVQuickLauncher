using System.ComponentModel;
using System.Diagnostics;
using Serilog;
using XIVLauncher.Common.Game;

namespace XIVLauncher.ArgReader;

public sealed class ArgReaderSession : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout  = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CommandTimeout  = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    private readonly ArgReaderChannel channel;
    private readonly SemaphoreSlim    commandLock = new(1, 1);
    private readonly TaskCompletionSource<HelloMessage> helloSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource<ArgReaderMessage>? pendingResponseSignal;
    private TaskCompletionSource<GoodbyeMessage>?   goodbyeSignal;
    private Process?                                process;
    private int?                                    lastReadProcessId;
    private int                                     isStopping;
    private int                                     isDisposed;

    private ArgReaderSession(ArgReaderChannel channel)
    {
        this.channel = channel;
        this.channel.MessageReceived += OnMessageReceived;
    }

    public static async Task<ArgReaderSession> StartAsync(CancellationToken cancellationToken = default)
    {
        var channelName = "XLArgReader" + Guid.NewGuid().ToString("N");
        var session     = new ArgReaderSession(new ArgReaderChannel(channelName));

        try
        {
            session.process = StartChildProcess(channelName);
            await WaitWithTimeoutAsync(session.helloSignal.Task, StartupTimeout, cancellationToken, "[ArgReaderIPC] 等待 ArgReader 握手超时");
            Log.Information("[ArgReaderIPC] ArgReader 会话已启动");
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task<GameArgumentInterop.LoginData> ReadLoginDataAsync(int processId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await commandLock.WaitAsync(cancellationToken);

        try
        {
            ThrowIfDisposed();

            var responseSignal = new TaskCompletionSource<ArgReaderMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref pendingResponseSignal, responseSignal, null) is not null)
                throw new InvalidOperationException("[ArgReaderIPC] 当前已有正在执行的命令");

            channel.Send(new ReadLoginDataRequest(processId));

            var response = await WaitWithTimeoutAsync(responseSignal.Task, CommandTimeout, cancellationToken, $"[ArgReaderIPC] 读取进程 {processId} 参数超时");
            if (response is not ReadLoginDataSucceeded succeeded)
                throw new InvalidOperationException($"[ArgReaderIPC] 收到意外响应: {response.GetType().Name}");

            lastReadProcessId = processId;
            Log.Information("[ArgReaderIPC] 已读取进程 {ProcessId} 参数", processId);
            return succeeded.Data;
        }
        finally
        {
            Interlocked.Exchange(ref pendingResponseSignal, null);
            commandLock.Release();
        }
    }

    public async Task StopAsync(bool killTargetProcess, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref isStopping, 1) != 0)
        {
            await WaitForProcessExitAsync(cancellationToken);
            return;
        }

        if (process is null)
            return;

        goodbyeSignal = new TaskCompletionSource<GoodbyeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!process.HasExited)
        {
            Log.Information("[ArgReaderIPC] 请求关闭 ArgReader, KillTargetProcess={KillTargetProcess}", killTargetProcess);
            channel.Send(new ShutdownRequest(killTargetProcess, killTargetProcess ? lastReadProcessId : null));
        }

        try
        {
            await Task.WhenAll
            (
                WaitWithTimeoutAsync(goodbyeSignal.Task, ShutdownTimeout, cancellationToken, "[ArgReaderIPC] 等待 Goodbye 超时"),
                WaitWithTimeoutAsync(process.WaitForExitAsync(cancellationToken), ShutdownTimeout, cancellationToken, "[ArgReaderIPC] 等待 ArgReader 退出超时")
            );

            Log.Information("[ArgReaderIPC] ArgReader 已正常退出");
        }
        catch (TimeoutException ex)
        {
            Log.Warning(ex, "[ArgReaderIPC] ArgReader 未在超时时间内退出, 将强制结束子进程");
            KillChildProcess();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref isDisposed, 1) != 0)
            return;

        try
        {
            await StopAsync(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ArgReaderIPC] 释放 ArgReader 会话时关闭子进程失败");
            KillChildProcess();
        }
        finally
        {
            channel.MessageReceived -= OnMessageReceived;
            channel.Dispose();
            process?.Dispose();
            commandLock.Dispose();
        }
    }

    private static Process StartChildProcess(string channelName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "XIVLauncher.ArgReader.exe");
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute  = false,
            Arguments        = channelName,
            WorkingDirectory = AppContext.BaseDirectory
        };

        if (!Debugger.IsAttached)
        {
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle    = ProcessWindowStyle.Hidden;
        }

        try
        {
            return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 ArgReader 子进程");
        }
        catch (Win32Exception ex)
        {
            Log.Error(ex, "[ArgReaderIPC] 启动 ArgReader 失败");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ArgReaderIPC] 启动 ArgReader 失败");
            throw new InvalidOperationException("启动 ArgReader 失败", ex);
        }
    }

    private void OnMessageReceived(ArgReaderMessage message)
    {
        switch (message)
        {
            case HelloMessage hello:
                helloSignal.TrySetResult(hello);
                return;

            case ReadLoginDataSucceeded succeeded:
                CompletePendingResponse(succeeded);
                return;

            case CommandFailed failed:
                CompletePendingFailure(failed);
                return;

            case GoodbyeMessage goodbye:
                goodbyeSignal?.TrySetResult(goodbye);
                return;

            default:
                Log.Warning("[ArgReaderIPC] 忽略未处理消息: {MessageType}", message.GetType().Name);
                return;
        }
    }

    private void CompletePendingResponse(ArgReaderMessage response)
    {
        var signal = Interlocked.Exchange(ref pendingResponseSignal, null);
        if (signal is null)
        {
            Log.Warning("[ArgReaderIPC] 收到无等待方的响应: {MessageType}", response.GetType().Name);
            return;
        }

        signal.TrySetResult(response);
    }

    private void CompletePendingFailure(CommandFailed failure)
    {
        var exception = new InvalidOperationException
        (
            string.IsNullOrWhiteSpace(failure.Details)
                ? failure.ErrorMessage
                : $"{failure.ErrorMessage}{Environment.NewLine}{failure.Details}"
        );

        var signal = Interlocked.Exchange(ref pendingResponseSignal, null);
        if (signal is not null)
        {
            signal.TrySetException(exception);
            return;
        }

        if (!helloSignal.Task.IsCompleted)
        {
            helloSignal.TrySetException(exception);
            return;
        }

        Log.Warning(exception, "[ArgReaderIPC] 收到无等待方的失败消息");
    }

    private async Task WaitForProcessExitAsync(CancellationToken cancellationToken)
    {
        if (process is null || process.HasExited)
            return;

        try
        {
            await WaitWithTimeoutAsync(process.WaitForExitAsync(cancellationToken), ShutdownTimeout, cancellationToken, "[ArgReaderIPC] 等待 ArgReader 退出超时");
        }
        catch (TimeoutException)
        {
            KillChildProcess();
        }
    }

    private void KillChildProcess()
    {
        if (process is null || process.HasExited)
            return;

        try
        {
            process.Kill();
            process.WaitForExit();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ArgReaderIPC] 强制结束 ArgReader 子进程失败");
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref isDisposed) != 0)
            throw new ObjectDisposedException(nameof(ArgReaderSession));
    }

    private static async Task WaitWithTimeoutAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken, string timeoutMessage)
    {
        try
        {
            await task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(timeoutMessage, ex);
        }
    }

    private static async Task<T> WaitWithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken, string timeoutMessage)
    {
        try
        {
            return await task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(timeoutMessage, ex);
        }
    }
}
