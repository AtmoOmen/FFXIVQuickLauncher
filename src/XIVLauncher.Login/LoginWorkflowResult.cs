namespace XIVLauncher.Login;

public sealed class LoginWorkflowResult
{
    public required GameLaunchContext GameLaunchContext { get; init; }

    public required bool IsAccountPersisted { get; init; }

    public required bool IsNewAccount { get; init; }

    public required bool UsedSavedWeGameToken { get; init; }

    public Func<Task<string>>? RefreshGameSessionIdByQuickLoginFunc { get; init; }
}
