using XIVLauncher.Login.Client;
using XIVLauncher.Login.Models;

namespace XIVLauncher.Login.Channels;

public interface ILoginChannel
{
    LoginType Type { get; }

    Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
}
