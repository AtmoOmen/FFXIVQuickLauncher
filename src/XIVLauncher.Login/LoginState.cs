namespace XIVLauncher.Login;

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
