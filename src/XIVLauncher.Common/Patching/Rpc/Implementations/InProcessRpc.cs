namespace XIVLauncher.Common.Patching.Rpc.Implementations;

public class InProcessRpc : IRpc, IDisposable
{
    private readonly string channelName;

    private static readonly Dictionary<string, List<InProcessRpc>> instanceMapping = new();

    public InProcessRpc(string channelName)
    {
        this.channelName = channelName;

        if (!instanceMapping.TryGetValue(channelName, out var instanceList))
        {
            instanceList = new List<InProcessRpc>();
            instanceMapping.Add(channelName, instanceList);
        }

        instanceList.Add(this);
    }

    #region Disposal

    public void Dispose() =>
        instanceMapping[channelName].Remove(this);

    #endregion

    public void SendMessage(PatcherIpcEnvelope envelope)
    {
        var list = instanceMapping[channelName];

        for (var i = 0; i < list.Count; i++)
        {
            var otherInstance = list[i];

            if (otherInstance == this)
                continue;

            otherInstance.Dispatch(envelope);
        }
    }

    private void Dispatch(PatcherIpcEnvelope envelope) =>
        MessageReceived?.Invoke(envelope);

    public event Action<PatcherIpcEnvelope> MessageReceived;
}
