using System.Collections.Generic;

namespace XIVLauncher.Common.Game.Patch.V3;

public sealed class V3GamePackageFileList
{
    public string BaseUrl       { get; set; } = string.Empty;
    public string BackupBaseUrl { get; set; } = string.Empty;
    public List<V3GamePackageFileEntry> FileList { get; set; } = [];
}
