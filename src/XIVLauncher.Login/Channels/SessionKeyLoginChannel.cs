namespace XIVLauncher.Login.Channels;

public sealed class SessionKeyLoginChannel
(
    LoginChannelContext context
) : ILoginChannel
{
    public LoginType Type => LoginType.QuickLogin;

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var guid = await context.GetGuidAsync().ConfigureAwait(false);
        var (sndaId, tgt, newAutoLoginSessionKey) = await context.UpdateAutoLoginSessionKeyAsync(guid, request.Secret).ConfigureAwait(false);

        var result = await context.GetJsonAsync("fastLogin.json", [$"tgt={tgt}", $"guid={guid}"]).ConfigureAwait(false);
        if (result.ReturnCode != 0)
            throw new LoginException(result.ReturnCode, result.Data.FailReason, true);

        sndaId = result.Data.SndaID;
        tgt    = result.Data.Tgt;

        try
        {
            context.BindLoginSessionRefresh(request.LoginSessionRefreshSink, tgt, guid);
            return LoginChannelContext.BuildOkLoginResult(request.Account, sndaId, null, newAutoLoginSessionKey, LoginType.QuickLogin, tgt, guid, request.DeviceProfile);
        }
        catch (Exception ex)
        {
            if (ex is LoginException loginException)
                loginException.RemoveQuickLoginSecret = true;

            throw;
        }
    }
}
