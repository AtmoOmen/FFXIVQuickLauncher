using System.Collections.Generic;

namespace XIVLauncher.Common.Game.Patch.V3;

public sealed class V3RemoteVersion
{
    public string                     BaseUrl       { get; set; } = string.Empty;
    public string                     BackupBaseUrl { get; set; } = string.Empty;
    public List<V3GameVersionArea>    Areas         { get; set; } = [];
    public List<V3GameVersionPackage> Packages      { get; set; } = [];
}
