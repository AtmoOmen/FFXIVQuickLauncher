using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Patching.Rpc;
using XIVLauncher.Common.Patching.Rpc.Implementations;

namespace XIVLauncher.Game;

public class RemoteArgReader : IDisposable
{
    public  ReaderState State { get; private set; } = ReaderState.NotStarted;
    private IRpc        rpc;

    private GameArgumentInterop.LoginData Data;

    private Process process;

    #region Disposal

    public void Dispose()
    {
        Log.Information("[ArgReaderIPC] Disposing");
        rpc.MessageReceived -= RemoteCallHandler;
    }

    #endregion

    public async Task Start()
    {
        var rpcName = "XLArgReader" + Guid.NewGuid();

        Log.Information("[ArgReaderIPC] Starting patcher with '{0}'", rpcName);

        rpc                 =  new SharedMemoryRpc(rpcName);
        rpc.MessageReceived += RemoteCallHandler;

        var path = Path.Combine(AppContext.BaseDirectory, "XIVLauncher.ArgReader.exe");

        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute  = false,
            Arguments        = rpcName,
            WorkingDirectory = AppContext.BaseDirectory
        };

        if (!Debugger.IsAttached)
        {
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle    = ProcessWindowStyle.Hidden;
        }

        State = ReaderState.NotReady;

        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            Log.Error(ex, "Could not launch Args Reader");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not launch Args Reader");
            throw new Exception("Start failed.", ex);
        }

        await WaitOn(ReaderState.Finish);
        Log.Information("[ArgReaderIPC] Start");
    }

    public async Task WaitOn(ReaderState state, int wait = 40)
    {
        Log.Information("[ArgReaderIPC] Waiting for state: {0}", state);
        await Task.Run
        (() =>
            {
                for (var i = 0; i < wait; i++)
                {
                    if (State == state)
                    {
                        Log.Information("[ArgReaderIPC] Desired state reached: {0}", state);
                        return;
                    }

                    Thread.Sleep(500);
                }

                Log.Error("[ArgReaderIPC] Reader RPC timed out.");
                throw new Exception("[ArgReaderIPC] Reader RPC timed out.");
            }
        );
    }

    public void Stop(bool killProcess)
    {
        Log.Information("[ArgReaderIPC] Stopping RPC with killProcess: {0}", killProcess);
        rpc.SendMessage
        (
            new PatcherIpcEnvelope
            {
                OpCode = PatcherIpcOpCode.Bye,
                Data   = killProcess
            }
        );
        Task.Run
        (() =>
            {
                Thread.Sleep(1000);

                try
                {
                    process?.Kill();
                }
                catch
                {
                }
            }
        );
    }

    public async Task OpenProcess(int pid)
    {
        Log.Information("[ArgReaderIPC] Opening process with PID: {0}", pid);
        State = ReaderState.Busy;
        rpc.SendMessage
        (
            new PatcherIpcEnvelope
            {
                OpCode = PatcherIpcOpCode.OpenProcess,
                Data   = pid
            }
        );

        await WaitOn(ReaderState.Finish);
        Log.Information($"[ArgReaderIPC] OpenProcess: {pid}");
    }

    public async Task<GameArgumentInterop.LoginData> ReadArgs()
    {
        Log.Information("[ArgReaderIPC] Reading arguments");
        State = ReaderState.Busy;
        rpc.SendMessage
        (
            new PatcherIpcEnvelope
            {
                OpCode = PatcherIpcOpCode.ReadArgs
            }
        );

        await WaitOn(ReaderState.Finish);
        Log.Information($"[ArgReaderIPC] ReadArgs: {Data}");
        return Data;
    }

    private void RemoteCallHandler(PatcherIpcEnvelope envelope)
    {
        Log.Information("[ArgReaderIPC] Received message with OpCode: {0}", envelope.OpCode);

        switch (envelope.OpCode)
        {
            case PatcherIpcOpCode.Hello:
                Log.Information("[ArgReaderIPC] GOT HELLO");
                State = ReaderState.Finish;
                break;

            case PatcherIpcOpCode.ArgReadOk:
                Log.Information($"[ArgReaderIPC] GOT ARGS: {envelope.Data}");
                Data  = (GameArgumentInterop.LoginData)envelope.Data;
                State = ReaderState.Finish;
                break;

            case PatcherIpcOpCode.ArgReadFail:
                Log.Information("[ArgReaderIPC] GOT FAILED");
                State = ReaderState.Failed;
                Stop(false);
                throw new Exception((string)envelope.Data);

            default:
                Log.Error("[ArgReaderIPC] Received unknown OpCode: {0}", envelope.OpCode);
                throw new ArgumentOutOfRangeException();
        }
    }

    public enum ReaderState
    {
        NotStarted,
        NotReady,
        Ready,
        Busy,
        Failed,
        Finish
    }
}
