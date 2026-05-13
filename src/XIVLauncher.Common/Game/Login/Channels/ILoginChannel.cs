namespace XIVLauncher.Common.Game.Login.Channels;

public interface ILoginChannel
{
    LoginType Type { get; }

    Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
}
