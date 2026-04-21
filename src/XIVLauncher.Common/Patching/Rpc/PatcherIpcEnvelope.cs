namespace XIVLauncher.Common.Patching.Rpc;

public class PatcherIpcEnvelope
{
    public PatcherIpcOpCode OpCode { get; set; }
    public object           Data   { get; set; }
}
