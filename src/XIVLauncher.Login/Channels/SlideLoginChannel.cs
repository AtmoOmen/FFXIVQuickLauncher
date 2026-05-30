namespace XIVLauncher.Login.Channels;

public sealed class SlideLoginChannel
(
    LoginChannelContext context
) : ILoginChannel
{
    public LoginType Type => LoginType.Slide;

    private const int SLIDE_EXPIRATION_TIME = 30 * 1000;
    private const int AUTO_LOGIN_KEEP_DAYS  = 30;

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        if (request.LoginCancellationTokenSource == null)
            throw new LoginException((int)LoginExceptionCode.SlideTimeoutOrCanceled, "登录取消令牌不能为空");

        var guid = await context.GetGuidAsync().ConfigureAwait(false);
        await context.CancelPushMessageLoginAsync(string.Empty, guid).ConfigureAwait(false);
        var (pushMsgSerialNum, pushMsgSessionKey, expiration) = await context.SendPushMessageAsync(request.Account, SLIDE_EXPIRATION_TIME).ConfigureAwait(false);
        request.ShowVerificationCode?.Invoke(pushMsgSerialNum);

        var (sndaId, tgt, autoLoginSessionKey) = await WaitForSlideAsync(pushMsgSessionKey, guid, expiration, request.LoginCancellationTokenSource, request.QuickLoginEnabled).ConfigureAwait(false);
        context.BindLoginSessionRefresh(request.LoginSessionRefreshSink, tgt, guid);
        return LoginChannelContext.BuildOkLoginResult(request.Account, sndaId, null, request.QuickLoginEnabled ? autoLoginSessionKey : null, LoginType.Slide, tgt, guid, request.DeviceProfile);
    }

    private async Task<(string sndaId, string tgt, string autoLoginSessionKey)> WaitForSlideAsync
    (
        string                  pushMsgSessionKey,
        string                  guid,
        CancellationTokenSource slideExpiration,
        CancellationTokenSource userCancel,
        bool                    autoLogin
    )
    {
        while (!slideExpiration.IsCancellationRequested && !userCancel.IsCancellationRequested)
        {
            var result = await context.GetJsonAsync
                         (
                             "pushMessageLogin.json",
                             autoLogin
                                 ? [$"pushMsgSessionKey={pushMsgSessionKey}", $"guid={guid}", "autoLoginFlag=1", $"autoLoginKeepTime={AUTO_LOGIN_KEEP_DAYS}"]
                                 : [$"pushMsgSessionKey={pushMsgSessionKey}", $"guid={guid}"]
                         ).ConfigureAwait(false);

            switch (result.ReturnCode)
            {
                case 0:
                    return (result.Data.SndaID, result.Data.Tgt, result.Data.QuickLoginSecret);

                case -10516808:
                    await Task.Delay(1000).ConfigureAwait(false);
                    continue;

                default:
                    throw new LoginException(result.ReturnCode, result.Data.FailReason);
            }
        }

        throw new LoginException((int)LoginExceptionCode.SlideTimeoutOrCanceled, "登录超时或被取消");
    }
}
