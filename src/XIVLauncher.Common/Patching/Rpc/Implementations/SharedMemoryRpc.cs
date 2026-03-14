using System;
using System.Text;
using Newtonsoft.Json;
using Serilog;
using SharedMemory;
using XIVLauncher.Common.PatcherIpc;

namespace XIVLauncher.Common.Patching.Rpc.Implementations;

public class SharedMemoryRpc : IRpc, IDisposable
{
    private readonly RpcBuffer rpcBuffer;

    public SharedMemoryRpc(string channelName) =>
        rpcBuffer = new RpcBuffer(channelName, RemoteCallHandler);

    #region Disposal

    public void Dispose() =>
        rpcBuffer?.Dispose();

    #endregion

    public void SendMessage(PatcherIpcEnvelope envelope)
    {
        var json = IpcHelpers.Base64Encode(JsonConvert.SerializeObject(envelope, IpcHelpers.JsonSettings));
        rpcBuffer.RemoteRequest(Encoding.ASCII.GetBytes(json));
    }

    private void RemoteCallHandler(ulong msgId, byte[] payload)
    {
        var json = IpcHelpers.Base64Decode(Encoding.ASCII.GetString(payload));
        Log.Information("[SHMEMRPC] IPC({0}): {1}", msgId, json);

        var msg = JsonConvert.DeserializeObject<PatcherIpcEnvelope>(json, IpcHelpers.JsonSettings);
        MessageReceived?.Invoke(msg);
    }

    public event Action<PatcherIpcEnvelope> MessageReceived;
}
