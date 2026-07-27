using XIVLauncher.Login.Models;

namespace XIVLauncher.Login.Workflow;

public sealed class LoginWorkflowResult
{
    public required GameLaunchContext GameLaunchContext { get; init; }

    public required bool IsAccountPersisted { get; init; }

    public required bool IsNewAccount { get; init; }

    public Func<Task<string>>? RefreshGameSessionIdByQuickLoginFunc { get; init; }
}
