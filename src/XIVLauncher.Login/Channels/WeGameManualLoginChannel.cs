namespace XIVLauncher.Login.Channels;

public sealed class WeGameManualLoginChannel
(
    LoginChannelContext context
) : ILoginChannel
{
    public LoginType Type => LoginType.WeGameManual;

    private const int AUTO_LOGIN_KEEP_DAYS = 30;

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var guid = await context.GetGuidAsync().ConfigureAwait(false);
        var (sndaId, tgt, autoLoginSessionKey) = await context.ThirdPartyLoginAsync(request.Account, request.Secret, request.AutoLogin, AUTO_LOGIN_KEEP_DAYS).ConfigureAwait(false);

        context.BindLoginSessionRefresh(request.LoginSessionRefreshSink, tgt, guid);
        var sessionId = await context.GetSessionIdAsync(tgt, guid).ConfigureAwait(false);
        return LoginChannelContext.BuildOkLoginResult(request.Account, sndaId, sessionId, request.AutoLogin ? autoLoginSessionKey : null, LoginType.WeGameManual);
    }
}
