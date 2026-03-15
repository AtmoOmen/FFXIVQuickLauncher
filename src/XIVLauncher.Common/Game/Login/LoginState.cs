namespace XIVLauncher.Common.Game.Login;

public enum LoginState
{
    Unknown,
    Ok,
    NeedsPatchGame,
    NeedsPatchBoot,
    NoService,
    NoTerms,
    NeedRetry
}
