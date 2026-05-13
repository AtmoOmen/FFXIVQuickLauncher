using System.Diagnostics;
using XIVLauncher.Common.Dalamud;

namespace XIVLauncher.Common.PlatformAbstractions;

public interface IDalamudRunner
{
    Process? Run
    (
        FileInfo                    runner,
        bool                        fakeLogin,
        bool                        noPlugins,
        bool                        noThirdPlugins,
        FileInfo                    gameExe,
        string                      gameArgs,
        IDictionary<string, string> environment,
        DalamudLoadMethod           loadMethod,
        DalamudStartInfo            dalamudStartInfo
    );
}
