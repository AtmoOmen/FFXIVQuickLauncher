namespace XIVLauncher.Login.Models;

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
