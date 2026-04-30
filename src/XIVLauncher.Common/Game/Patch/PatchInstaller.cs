using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Serilog;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.Patching;
using XIVLauncher.Common.Patching.Rpc;
using XIVLauncher.Common.Patching.Rpc.Implementations;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Game.Patch;

public class PatchInstaller : IDisposable
{
    public           InstallerState State { get; private set; } = InstallerState.NotStarted;
    private readonly bool           keepPatches;
    private          IRpc           rpc;

    private RemotePatchInstaller? internalPatchInstaller;

    public PatchInstaller(bool keepPatches) =>
        this.keepPatches = keepPatches;

    #region Disposal

    public void Dispose() =>
        Stop();

    #endregion

    public void StartIfNeeded(bool external = true)
    {
        var rpcName = "XLPatcher" + Guid.NewGuid();

        Log.Information("[PATCHERIPC] Starting patcher with '{0}'", rpcName);

        if (external)
        {
            rpc                 =  new SharedMemoryRpc(rpcName);
            rpc.MessageReceived += RemoteCallHandler;

            var path = Path.Combine
            (
                AppContext.BaseDirectory,
                "PatchInstaller",
                "XIVLauncher.PatchInstaller.exe"
            );

            var startInfo = PatchInstallerProcessStartInfo.Create
            (
                path,
                $"rpc {rpcName}",
                !EnvironmentSettings.IsNoRunas && Environment.OSVersion.Version.Major >= 6,
                PatchInstallerProcessStartInfo.GetDefaultDotNetRootPath()
            );

            if (!Debugger.IsAttached)
            {
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle    = ProcessWindowStyle.Hidden;
            }

            State = InstallerState.NotReady;

            try
            {
                Process.Start(startInfo);
            }
            catch (Win32Exception ex) when (PlatformHelpers.IsWindowsErrorCancelled(ex))
            {
                throw new OperationCanceledException();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not launch Patch Installer");
                throw new PatchInstallerException("Start failed.", ex);
            }
        }
        else
        {
            rpc                 =  new InProcessRpc(rpcName);
            rpc.MessageReceived += RemoteCallHandler;

            internalPatchInstaller = new RemotePatchInstaller(new InProcessRpc(rpcName));
            internalPatchInstaller.Start();
        }
    }

    public void WaitOnHello()
    {
        for (var i = 0; i < 40; i++)
        {
            if (State == InstallerState.Ready)
                return;

            Thread.Sleep(500);
        }

        throw new PatchInstallerException("Installer RPC timed out.");
    }

    public void Stop()
    {
        if (State == InstallerState.NotReady || State == InstallerState.NotStarted || State == InstallerState.Busy)
            return;

        rpc.SendMessage
        (
            new PatcherIpcEnvelope
            {
                OpCode = PatcherIpcOpCode.Bye
            }
        );
    }

    public void StartInstall(DirectoryInfo gameDirectory, FileInfo file, PatchListEntry patch)
    {
        State = InstallerState.Busy;
        rpc.SendMessage
        (
            new PatcherIpcEnvelope
            {
                OpCode = PatcherIpcOpCode.StartInstall,
                Data = new PatcherIpcStartInstall
                {
                    GameDirectory = gameDirectory,
                    PatchFile     = file,
                    Repo          = patch.GetRepo(),
                    VersionId     = patch.VersionId,
                    KeepPatch     = keepPatches
                }
            }
        );
    }

    public void FinishInstall(DirectoryInfo gameDirectory)
    {
        rpc.SendMessage
        (
            new PatcherIpcEnvelope
            {
                OpCode = PatcherIpcOpCode.Finish,
                Data   = gameDirectory.FullName
            }
        );
    }

    private void RemoteCallHandler(PatcherIpcEnvelope envelope)
    {
        switch (envelope.OpCode)
        {
            case PatcherIpcOpCode.Hello:
                //_client.Initialize(_clientPort);
                Log.Information("[PATCHERIPC] GOT HELLO");
                State = InstallerState.Ready;
                break;

            case PatcherIpcOpCode.InstallOk:
                Log.Information("[PATCHERIPC] INSTALL OK");
                State = InstallerState.Ready;
                break;

            case PatcherIpcOpCode.InstallFailed:
                State = InstallerState.Failed;
                OnFail?.Invoke();
                Stop();
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public enum InstallerState
    {
        NotStarted,
        NotReady,
        Ready,
        Busy,
        Failed
    }

    public event Action OnFail;
}
