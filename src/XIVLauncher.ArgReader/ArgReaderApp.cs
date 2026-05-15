using System.Diagnostics;
using Serilog;
using XIVLauncher.Common.Game;

namespace XIVLauncher.ArgReader;

internal sealed class ArgReaderApp : IAsyncDisposable
{
    private readonly ArgReaderChannel channel;
    private readonly TaskCompletionSource exitSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int isStopping;

    public ArgReaderApp(string channelName)
    {
        channel                 =  new ArgReaderChannel(channelName);
        channel.MessageReceived += OnMessageReceived;
        Log.Information("[ArgReader] 已连接 IPC");
    }

    public Task RunAsync()
    {
        Send(new HelloMessage());
        Log.Information("[ArgReader] 已发送 Hello");
        return exitSignal.Task;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref isStopping, 1) == 0)
        {
            channel.MessageReceived -= OnMessageReceived;
            channel.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void OnMessageReceived(ArgReaderMessage message)
    {
        try
        {
            switch (message)
            {
                case ReadLoginDataRequest request:
                    HandleReadLoginData(request);
                    return;

                case ShutdownRequest request:
                    HandleShutdown(request);
                    return;

                default:
                    Log.Warning("[ArgReader] 未处理消息: {MessageType}", message.GetType().Name);
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ArgReader] 处理消息失败");
            SendFailure(ex);
        }
    }

    private void HandleReadLoginData(ReadLoginDataRequest request)
    {
        Log.Information("[ArgReader] 读取进程参数: {ProcessId}", request.ProcessId);

        using var process = Process.GetProcessById(request.ProcessId);
        var reader = new GameArgumentInterop.Reader(process);
        var data   = reader.ReadLoginData();

        Send(new ReadLoginDataSucceeded(data));
        Log.Information("[ArgReader] 已发送 ReadLoginDataSucceeded");
    }

    private void HandleShutdown(ShutdownRequest request)
    {
        if (request.KillTargetProcess)
        {
            TryKillProcess(request.TargetProcessId);
            KillResidualProcesses();
        }

        Send(new GoodbyeMessage());
        Log.Information("[ArgReader] 完成");
        SignalExit();
    }

    private void SendFailure(Exception ex) =>
        Send(new CommandFailed(ex.Message, ex.ToString()));

    private void Send(ArgReaderMessage message) =>
        channel.Send(message);

    private static void TryKillProcess(int? processId)
    {
        if (!processId.HasValue)
            return;

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ArgReader] 关闭目标游戏进程失败: {ProcessId}", processId);
        }
    }

    private static void KillResidualProcesses()
    {
        KillByName("sdologin");
        KillByName("SdoLoginComServer");
    }

    private static void KillByName(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[ArgReader] 关闭进程失败: {ProcessName}", processName);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private void SignalExit() =>
        exitSignal.TrySetResult();
}
