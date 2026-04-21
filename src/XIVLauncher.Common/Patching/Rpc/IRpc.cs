using System;

namespace XIVLauncher.Common.Patching.Rpc;

public interface IRpc
{
    void SendMessage(PatcherIpcEnvelope envelope);

    event Action<PatcherIpcEnvelope> MessageReceived;
}
