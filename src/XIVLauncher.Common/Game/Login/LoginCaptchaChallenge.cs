using System;
using System.Threading;
using System.Threading.Tasks;

namespace XIVLauncher.Common.Game.Login;

public sealed class LoginCaptchaChallenge
{
    public string Title { get; init; } = string.Empty;

    public string Prompt { get; init; } = string.Empty;

    public byte[]? ImageBytes { get; init; }

    public Func<CancellationToken, Task<LoginCaptchaChallenge>>? RefreshAsync { get; init; }
}
