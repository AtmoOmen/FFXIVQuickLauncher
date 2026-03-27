using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Game.Exceptions;

namespace XIVLauncher.Common.Game.Login.Channels;

public sealed class StaticLoginChannel
(
    LoginChannelContext context
) : ILoginChannel
{
    public LoginType Type => LoginType.Static;

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var guid       = await context.GetGuidAsync().ConfigureAwait(false);
        var macAddress = request.DeviceProfile.MacHash;
        var result = await context.GetJsonAsync
                     (
                         "staticLogin.json",
                         [
                             "checkCodeFlag=1", "encryptFlag=0", $"inputUserId={request.Account}", $"password={request.Secret}", $"mac={macAddress}", $"guid={guid}",
                             "inputUserType=0&accountDomain=1&autoLoginFlag=0&autoLoginKeepTime=0&supportPic=2"
                         ]
                     ).ConfigureAwait(false);

        if (result.ReturnCode != 0 || result.ErrorType != 0)
            throw new LoginException(result.ReturnCode, result.Data.FailReason);

        if (string.IsNullOrEmpty(result.Data.Tgt))
            throw new LoginException((int)LoginExceptionCode.StaticNeedCaptcha, "静态登录需要输入验证码, 目前暂未支持, 请选用其他方式登录");

        var sndaId = result.Data.SndaID;
        var tgt    = result.Data.Tgt;

        context.BindDCTravelSessionRefresh(request.DCTravelClient, tgt, guid);
        var sessionId = await context.GetSessionIdAsync(tgt, guid).ConfigureAwait(false);
        return LoginChannelContext.BuildOkLoginResult(request.Account, sndaId, sessionId, null, LoginType.Static);
    }
}
