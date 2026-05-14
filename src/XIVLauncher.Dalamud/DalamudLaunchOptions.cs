namespace XIVLauncher.Dalamud;

public sealed record DalamudLaunchOptions
(
    DalamudLoadMethod LoadMethod,
    int               DelayInitializeMs,
    bool              FakeLogin,
    bool              NoPlugins,
    bool              NoThirdPlugins
);
