using Newtonsoft.Json;

namespace XIVLauncher.Common.Patching.Rpc;

public class PatcherIpcStartInstall
{
    public string     PatchFileDTO     { get; set; }
    public Repository Repo             { get; set; }
    public string     VersionId        { get; set; }
    public string     GameDirectoryDTO { get; set; }
    public bool       KeepPatch        { get; set; }

    [JsonIgnore] public FileInfo PatchFile
    {
        get => new(PatchFileDTO);
        set => PatchFileDTO = value.FullName;
    }

    [JsonIgnore] public DirectoryInfo GameDirectory
    {
        get => new(GameDirectoryDTO);
        set => GameDirectoryDTO = value.FullName;
    }
}
