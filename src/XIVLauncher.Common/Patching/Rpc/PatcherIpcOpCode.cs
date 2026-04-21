namespace XIVLauncher.Common.Patching.Rpc;

public enum PatcherIpcOpCode
{
    Hello,
    Bye,
    StartInstall,
    InstallRunning,
    InstallOk,
    InstallFailed,
    Finish,

    //for ArgReader
    OpenProcess,
    ReadArgs,
    ArgReadFail,
    ArgReadOk
}
