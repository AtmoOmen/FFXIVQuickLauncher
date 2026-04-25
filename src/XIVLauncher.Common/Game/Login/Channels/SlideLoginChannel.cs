using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Game.Exceptions;

namespace XIVLauncher.Common.Game.Login.Channels;

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

        var    guid              = await context.GetGuidAsync().ConfigureAwait(false);
        string pushMsgSessionKey = string.Empty;

        try
        {
            await context.GetPushMessageStatusAsync(request.Account, guid, request.AutoLogin, AUTO_LOGIN_KEEP_DAYS).ConfigureAwait(false);
            var (pushMsgSerialNum, newPushMsgSessionKey, expiration) = await context.SendPushMessageAsync(request.Account, guid, SLIDE_EXPIRATION_TIME).ConfigureAwait(false);
            pushMsgSessionKey = newPushMsgSessionKey;
            request.ShowVerificationCode?.Invoke(pushMsgSerialNum);

            var (sndaId, tgt, autoLoginSessionKey) = await WaitForSlideAsync(pushMsgSessionKey, guid, expiration, request.LoginCancellationTokenSource, request.AutoLogin).ConfigureAwait(false);
            pushMsgSessionKey = string.Empty;
            context.BindDCTravelSessionRefresh(request.DCTravelClient, tgt, guid);
            var sessionId = await context.GetSessionIdAsync(tgt, guid).ConfigureAwait(false);
            return LoginChannelContext.BuildOkLoginResult(request.Account, sndaId, sessionId, request.AutoLogin ? autoLoginSessionKey : null, LoginType.Slide);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(pushMsgSessionKey))
            {
                try
                {
                    await context.CancelPushMessageLoginAsync(pushMsgSessionKey).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[SlideLoginChannel] 取消叨鱼推送会话失败");
                }
            }

            throw;
        }
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
                             [$"pushMsgSessionKey={pushMsgSessionKey}", $"keepLoginFlag={(autoLogin ? 1 : 0)}", $"guid={guid}"]
                         ).ConfigureAwait(false);

            switch (result.ReturnCode)
            {
                case 0:
                    return (result.Data.SndaID, result.Data.Tgt, result.Data.AutoLoginSessionKey);

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
