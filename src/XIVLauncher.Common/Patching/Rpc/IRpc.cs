using System;
using XIVLauncher.Common.PatcherIpc;

namespace XIVLauncher.Common.Patching.Rpc;

public interface IRpc
{
    void SendMessage(PatcherIpcEnvelope envelope);

    event Action<PatcherIpcEnvelope> MessageReceived;
}
