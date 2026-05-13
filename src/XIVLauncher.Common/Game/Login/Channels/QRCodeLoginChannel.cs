using XIVLauncher.Common.Game.Exceptions;

namespace XIVLauncher.Common.Game.Login.Channels;

public sealed class QRCodeLoginChannel
(
    LoginChannelContext context
) : ILoginChannel
{
    public        LoginType Type => LoginType.QRCode;
    private const int       QR_CODE_EXPIRATION_TIME = 300 * 1000;
    private const int       AUTO_LOGIN_KEEP_DAYS    = 30;

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        if (request.LoginCancellationTokenSource == null)
            throw new LoginException((int)LoginExceptionCode.ScanTimeoutOrCanceled, "登录取消令牌不能为空");

        var     guid    = await context.GetGuidAsync().ConfigureAwait(false);
        string? sndaId  = null;
        string? tgt     = null;
        string? account = null;

        while (!request.LoginCancellationTokenSource.IsCancellationRequested)
        {
            var (codeKey, qrCode, expiration) = await context.GetQRCodeAsync(QR_CODE_EXPIRATION_TIME).ConfigureAwait(false);
            request.ShowQRCode?.Invoke(qrCode);
            (sndaId, tgt, account) = await WaitForScanAsync(codeKey, guid, expiration, request.LoginCancellationTokenSource).ConfigureAwait(false);
            if (sndaId != null)
                break;
        }

        var newAccount = await context.GetAccountGroupAsync(tgt!, sndaId!).ConfigureAwait(false);
        account = string.IsNullOrEmpty(account) ? newAccount : account;
        string? autoLoginSessionKey = null;
        if (request.AutoLogin)
            (tgt, autoLoginSessionKey) = await context.AccountGroupLoginAsync(tgt!, sndaId!, AUTO_LOGIN_KEEP_DAYS).ConfigureAwait(false);

        context.BindDCTravelSessionRefresh(request.DCTravelClient, tgt!, guid);
        var sessionId = await context.GetSessionIdAsync(tgt!, guid).ConfigureAwait(false);
        return LoginChannelContext.BuildOkLoginResult(account!, sndaId!, sessionId, request.AutoLogin ? autoLoginSessionKey : null, LoginType.QRCode);
    }

    private async Task<(string sndaId, string tgt, string account)> WaitForScanAsync(string codeKey, string guid, CancellationTokenSource qrCodeExpiration, CancellationTokenSource userCancel)
    {
        while (!qrCodeExpiration.IsCancellationRequested && !userCancel.IsCancellationRequested)
        {
            var result = await context.GetJsonAsync
                         (
                             "codeKeyLogin.json",
                             [$"codeKey={codeKey}", $"guid={guid}", "autoLoginFlag=1", $"autoLoginKeepTime={AUTO_LOGIN_KEEP_DAYS}", "maxsize=97"]
                         ).ConfigureAwait(false);

            if (result.ReturnCode == 0 && result.Data.NextAction == 0)
                return (result.Data.SndaID, result.Data.Tgt, result.Data.InputUserID);

            if (result.ReturnCode == (int)LoginExceptionCode.QrCodeVerifyFailed)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                continue;
            }

            if (result.Data.FailReason == "二维码不存在或已过期，请重试")
                throw new LoginException((int)LoginExceptionCode.ScanTimeoutOrCanceled, result.Data.FailReason);

            throw new OAuthLoginException(result.Data.FailReason);
        }

        if (userCancel.IsCancellationRequested)
            throw new LoginException((int)LoginExceptionCode.ScanTimeoutOrCanceled, "登录超时或被取消");

        throw new LoginException((int)LoginExceptionCode.ScanTimeoutOrCanceled, "二维码不存在或已过期，请重试");
    }
}
