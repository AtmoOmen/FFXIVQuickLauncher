using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using Serilog;
using Serilog.Events;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Patching.Rpc;
using XIVLauncher.Common.Patching.Rpc.Implementations;

namespace XIVLauncher.ArgReader;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ConfigureLogger();

        if (args.Length != 1)
        {
            Log.Error("[ArgReader] 参数错误");
            return -1;
        }

        try
        {
            await using var app = new ArgReaderApp(args[0]);
            await app.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[ArgReader] 致命错误");
            return -1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ConfigureLogger()
    {
        Log.Logger = new LoggerConfiguration()
                     .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Fatal)
                     .WriteTo.File(Path.Combine(Paths.RoamingPath, "argReader.log"))
                     .WriteTo.Debug()
                     .MinimumLevel.Verbose()
                     .CreateLogger();
    }
}

internal sealed class ArgReaderApp : IAsyncDisposable
{
    private readonly SharedMemoryRpc                                                rpc;
    private readonly TaskCompletionSource                                           exitSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FrozenDictionary<PatcherIpcOpCode, Action<PatcherIpcEnvelope>> handlers;
    private          int                                                            isStopping;
    private          GameArgumentInterop.Reader?                                    argReader;

    public ArgReaderApp(string channelName)
    {
        rpc                 =  new SharedMemoryRpc(channelName);
        rpc.MessageReceived += OnMessageReceived;
        handlers = new Dictionary<PatcherIpcOpCode, Action<PatcherIpcEnvelope>>
        {
            [PatcherIpcOpCode.Bye]         = HandleBye,
            [PatcherIpcOpCode.OpenProcess] = HandleOpenProcess,
            [PatcherIpcOpCode.ReadArgs]    = HandleReadArgs
        }.ToFrozenDictionary();

        Log.Information("[ArgReader] 已连接 IPC");
    }

    public Task RunAsync()
    {
        Send
        (
            new PatcherIpcEnvelope
            {
                OpCode = PatcherIpcOpCode.Hello,
                Data   = DateTime.Now
            }
        );
        
        Log.Information("[ArgReader] 已发送 Hello");
        return exitSignal.Task;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref isStopping, 1) == 0)
        {
            rpc.MessageReceived -= OnMessageReceived;
            rpc.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void OnMessageReceived(PatcherIpcEnvelope envelope)
    {
        try
        {
            if (handlers.TryGetValue(envelope.OpCode, out var handler))
            {
                handler(envelope);
                return;
            }

            Log.Warning("[ArgReader] 未处理的 OPCode: {OpCode}", envelope.OpCode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ArgReader] 处理消息失败");
            SendFailure(ex);
        }
    }

    private void HandleBye(PatcherIpcEnvelope envelope)
    {
        if (envelope.Data is bool killTargetProcess && killTargetProcess)
        {
            argReader?.KillProcess();
            KillResidualProcesses();
        }

        Log.Information("[ArgReader] 完成");
        SignalExit();
    }

    private void HandleOpenProcess(PatcherIpcEnvelope envelope)
    {
        Log.Information("[ArgReader] 打开进程: {ProcessId}", envelope.Data);
        
        var processID = ConvertToProcessID(envelope.Data);
        var process   = Process.GetProcessById(processID);
        argReader = new GameArgumentInterop.Reader(process);
        
        Send
        (
            new PatcherIpcEnvelope
            {
                OpCode = PatcherIpcOpCode.ArgReadOk,
                Data   = new GameArgumentInterop.LoginData()
            }
        );
        
        Log.Information("[ArgReader] 已发送 ArgReadOk");
    }

    private void HandleReadArgs(PatcherIpcEnvelope _)
    {
        if (argReader is null)
            throw new InvalidOperationException("[ArgReader] 未打开进程");

        Log.Information("[ArgReader] 读取参数");
        
        var data = argReader.ReadLoginData();
        Send
        (
            new PatcherIpcEnvelope
            {
                OpCode = PatcherIpcOpCode.ArgReadOk,
                Data   = data
            }
        );
        
        Log.Information("[ArgReader] 已发送 ArgReadOk");
    }

    private static int ConvertToProcessID(object? processIdData)
    {
        return processIdData switch
        {
            int processId        => processId,
            long processId       => checked((int)processId),
            string processIdText => int.Parse(processIdText, CultureInfo.InvariantCulture),
            _                    => throw new InvalidCastException($"[ArgReader] 不支持的进程 ID 类型: {processIdData?.GetType().FullName ?? "<null>"}")
        };
    }

    private void SendFailure(Exception ex)
    {
        Send
        (
            new PatcherIpcEnvelope
            {
                OpCode = PatcherIpcOpCode.ArgReadFail,
                Data   = ex.ToString()
            }
        );
    }

    private void Send(PatcherIpcEnvelope envelope) =>
        rpc.SendMessage(envelope);

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
