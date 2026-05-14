using System.Diagnostics;

namespace XIVLauncher.Dalamud;

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
