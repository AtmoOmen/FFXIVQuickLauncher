namespace XIVLauncher.Common.Patching.Rpc;

public enum PatcherIpcOpCode
{
    Hello,
    Bye,
    OpenProcess,
    ReadArgs,
    ArgReadFail,
    ArgReadOk
}
