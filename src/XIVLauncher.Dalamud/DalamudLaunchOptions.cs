namespace XIVLauncher.Dalamud;

public sealed record DalamudLaunchOptions
{
    /// <summary>
    ///     注入延迟上限（毫秒）, 须明显低于注入器等待游戏窗口的超时, 避免拉起游戏时卡死
    /// </summary>
    public const int MAX_DELAY_INITIALIZE_MS = 10 * 1000;

    public DalamudLaunchOptions
    (
        DalamudLoadMethod loadMethod,
        int               delayInitializeMs,
        bool              fakeLogin,
        bool              noPlugins,
        bool              noThirdPlugins
    )
    {
        LoadMethod        = loadMethod;
        DelayInitializeMs = Math.Clamp(delayInitializeMs, 0, MAX_DELAY_INITIALIZE_MS);
        FakeLogin         = fakeLogin;
        NoPlugins         = noPlugins;
        NoThirdPlugins    = noThirdPlugins;
    }

    public DalamudLoadMethod LoadMethod        { get; }
    public int               DelayInitializeMs { get; }
    public bool              FakeLogin         { get; }
    public bool              NoPlugins         { get; }
    public bool              NoThirdPlugins    { get; }
}
