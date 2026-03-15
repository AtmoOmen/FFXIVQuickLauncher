using System.Threading;
using System.Threading.Tasks;

namespace XIVLauncher.Common.Game.Login.Channels;

public sealed class SIDLoginChannel
(
    LoginChannelContext context
) : ILoginChannel
{
    public LoginType Type => LoginType.WeGameSID;

    public Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        _ = context;
        var oath = new OAuthLoginResult
        {
            SndaID       = request.Account,
            SessionID    = request.Secret,
            MaxExpansion = Constants.MaxExpansion,
            LoginType    = LoginType.WeGameSID
        };

        var result = new LoginResult
        {
            OAuthLogin = oath,
            State      = LoginState.Ok
        };

        return Task.FromResult(result);
    }
}
