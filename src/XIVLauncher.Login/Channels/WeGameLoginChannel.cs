namespace XIVLauncher.Login.Channels;

public sealed class WeGameLoginChannel
(
    LoginChannelContext context
) : ILoginChannel
{
    public LoginType Type => LoginType.WeGame;

    private const int AUTO_LOGIN_KEEP_DAYS = 30;

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var guid = await context.GetGuidAsync().ConfigureAwait(false);
        var (sndaId, tgt, autoLoginSessionKey) = await context.ThirdPartyLoginAsync(request.Account, request.Secret, request.QuickLoginEnabled, AUTO_LOGIN_KEEP_DAYS).ConfigureAwait(false);

        context.BindLoginSessionRefresh(request.LoginSessionRefreshSink, tgt, guid);
        return LoginChannelContext.BuildOkLoginResult(request.Account, sndaId, null, request.QuickLoginEnabled ? autoLoginSessionKey : null, LoginType.WeGame, tgt, guid, request.DeviceProfile);
    }
}
