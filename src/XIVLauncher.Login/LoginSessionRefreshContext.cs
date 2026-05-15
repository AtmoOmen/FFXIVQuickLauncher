namespace XIVLauncher.Login;

public sealed class LoginSessionRefreshContext
{
    public required Func<Task<string>> RefreshGameSessionIdAsync     { get; init; }
    public required Func<Task<string>> RefreshDcTravelSessionIdAsync { get; init; }
}
