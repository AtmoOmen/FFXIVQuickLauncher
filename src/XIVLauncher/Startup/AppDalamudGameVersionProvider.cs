using System.IO;
using XIVLauncher.Common;
using XIVLauncher.Dalamud;

namespace XIVLauncher.Startup;

internal sealed class AppDalamudGameVersionProvider : IDalamudGameVersionProvider
{
    public string GetVersion(DirectoryInfo gamePath, bool isBck = false) =>
        Repository.Ffxiv.GetVer(gamePath, isBck);
}
