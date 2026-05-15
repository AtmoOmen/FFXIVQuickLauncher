namespace XIVLauncher.Login;

public sealed class LoginWorkflowRequest
{
    public required LoginType LoginType { get; init; }

    public required string Username { get; init; }

    public required string Password { get; init; }

    public required bool DoingAutoLogin { get; init; }

    public required bool ReadWeGameInfo { get; init; }

    public required LoginAfterAction Action { get; init; }

    public required LoginArea CurrentArea { get; init; }

    public required LoginArea[] LoginAreas { get; init; }

    public required CancellationTokenSource LoginCancellationTokenSource { get; init; }

    public required ILoginSessionRefreshSink? LoginSessionRefreshSink { get; init; }

    public required ILoginWorkflowInteraction Interaction { get; init; }

    public required bool RequireDeviceProfileSetupForNewLogin { get; init; }

    public required Func<CancellationToken, Task<LoginResult>> CheckGameUpdateAsync { get; init; }
}
