using System.Text;

namespace XIVLauncher.Login.Channels;

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

        if (result.ReturnCode == (int)LoginExceptionCode.RiskEnvironment)
            result = await LoginBySafePhoneSmsAsync(request, result).ConfigureAwait(false);

        if (NeedsStaticCaptcha(result))
            result = await LoginByStaticCaptchaAsync(request, guid, result).ConfigureAwait(false);

        if (result.ReturnCode != 0 || result.ErrorType != 0)
            throw new LoginException(result.ReturnCode, result.Data.FailReason);

        if (string.IsNullOrEmpty(result.Data.Tgt))
            throw new LoginException((int)LoginExceptionCode.StaticNeedCaptcha, "静态登录需要额外验证码挑战，但服务端没有返回可显示的验证码图片，当前暂未支持该类型。");

        var sndaId = result.Data.SndaID;
        var tgt    = result.Data.Tgt;

        context.BindLoginSessionRefresh(request.LoginSessionRefreshSink, tgt, guid);
        var sessionId = await context.GetSessionIdAsync(tgt, guid).ConfigureAwait(false);
        return LoginChannelContext.BuildOkLoginResult(request.Account, sndaId, sessionId, null, LoginType.Static);
    }

    private async Task<LoginResponse> LoginByStaticCaptchaAsync(LoginRequest request, string guid, LoginResponse result)
    {
        var captchaGuid = !string.IsNullOrWhiteSpace(result.Data.Guid) ? result.Data.Guid : guid;

        while (NeedsStaticCaptcha(result))
        {
            var prompt      = await BuildStaticCaptchaPromptAsync(result).ConfigureAwait(false);
            var captchaText = request.PromptCaptchaInput?.Invoke(prompt);
            if (string.IsNullOrWhiteSpace(captchaText))
                throw new LoginException((int)LoginExceptionCode.CaptchaVerificationCanceled, "已取消登录验证码输入。");

            request.ShowLoginMessage?.Invoke("正在校验登录验证码…");

            result = await context.CheckCodeLoginAsync(captchaGuid, captchaText.Trim(), request.QuickLoginEnabled).ConfigureAwait(false);
            if (result.ReturnCode != 0 || result.ErrorType != 0)
                throw new LoginException(result.ReturnCode, result.Data.FailReason);

            if (!string.IsNullOrWhiteSpace(result.Data.Guid))
                captchaGuid = result.Data.Guid;
        }

        if (string.IsNullOrWhiteSpace(result.Data.Tgt))
            throw new LoginException((int)LoginExceptionCode.StaticNeedCaptcha, "登录验证码校验完成后，服务端没有返回登录票据。");

        return result;
    }

    private async Task<LoginResponse> LoginBySafePhoneSmsAsync(LoginRequest request, LoginResponse staticLoginResponse)
    {
        request.ShowLoginMessage?.Invoke("检测到安全手机验证，正在准备短信验证流程…");

        _ = await context.GetSafePhoneSystemConfigAsync().ConfigureAwait(false);

        var initResult = await context.InitSafePhoneSmsLoginAsync(request.Account, staticLoginResponse.Data.FlowId).ConfigureAwait(false);
        if (initResult.ReturnCode != 0 || initResult.ErrorType != 0)
            throw new LoginException(initResult.ReturnCode, initResult.Data.FailReason);

        var flowId = initResult.Data.FlowId ?? staticLoginResponse.Data.FlowId;
        if (string.IsNullOrWhiteSpace(flowId))
            throw new LoginException((int)LoginExceptionCode.RiskEnvironment, "检测到安全手机验证，但服务端没有返回可用的验证流程标识。");

        if (RequiresCaptchaChallenge(initResult))
            throw new LoginException((int)LoginExceptionCode.RiskEnvironment, BuildCaptchaRequiredMessage(initResult));

        request.ShowLoginMessage?.Invoke(BuildSmsPendingMessage(initResult));

        var confirmSendResult = await context.ConfirmSafePhoneSendAsync(flowId).ConfigureAwait(false);
        if (confirmSendResult.ReturnCode != 0 || confirmSendResult.ErrorType != 0)
            throw new LoginException(confirmSendResult.ReturnCode, confirmSendResult.Data.FailReason);

        flowId = confirmSendResult.Data.FlowId ?? flowId;

        var verifyCode = request.PromptTextInput?.Invoke
        (
            BuildSmsInputPrompt(initResult),
            "安全手机短信验证",
            string.Empty
        );

        if (string.IsNullOrWhiteSpace(verifyCode))
            throw new LoginException((int)LoginExceptionCode.SafePhoneVerificationCanceled, "已取消安全手机短信验证。");

        request.ShowLoginMessage?.Invoke("正在校验安全手机短信验证码…");

        var confirmLoginResult = await context.ConfirmSafePhoneLoginAsync(request.Account, flowId, verifyCode.Trim(), request.QuickLoginEnabled).ConfigureAwait(false);
        if (confirmLoginResult.ReturnCode != 0 || confirmLoginResult.ErrorType != 0)
            throw new LoginException(confirmLoginResult.ReturnCode, confirmLoginResult.Data.FailReason);

        if (string.IsNullOrWhiteSpace(confirmLoginResult.Data.Tgt))
            throw new LoginException((int)LoginExceptionCode.RiskEnvironment, "安全手机短信验证完成后，服务端没有返回登录票据。");

        request.ShowLoginMessage?.Invoke("安全手机短信验证成功，正在继续登录游戏…");
        return confirmLoginResult;
    }

    private static bool RequiresCaptchaChallenge(LoginResponse result) =>
        result.Data.CaptchaParams != null
        || !string.IsNullOrWhiteSpace(result.Data.CheckCodeUrl)
        || !string.IsNullOrWhiteSpace(result.Data.CheckCodeSessionKey)
        || !string.IsNullOrWhiteSpace(result.Data.PicUrl);

    private static bool NeedsStaticCaptcha(LoginResponse result) =>
        string.IsNullOrWhiteSpace(result.Data.Tgt)
        && HasCaptchaImage(result);

    private static bool HasCaptchaImage(LoginResponse result) =>
        !string.IsNullOrWhiteSpace(GetCaptchaImageUrl(result));

    private static string? GetCaptchaImageUrl(LoginResponse result) =>
        !string.IsNullOrWhiteSpace(result.Data.PicUrl) ? result.Data.PicUrl : result.Data.CheckCodeUrl;

    private static string BuildCaptchaRequiredMessage(LoginResponse result)
    {
        var builder = new StringBuilder("当前账号命中了安全手机风控，但发送短信前还需要先完成额外验证码挑战，暂未支持这一分支。");

        if (!string.IsNullOrWhiteSpace(result.Data.SafePhoneTip))
            builder.Append($"\n提示：{result.Data.SafePhoneTip}");

        if (!string.IsNullOrWhiteSpace(result.Data.MobileMask))
            builder.Append($"\n安全手机：{result.Data.MobileMask}");

        return builder.ToString();
    }

    private static string BuildSmsPendingMessage(LoginResponse result)
    {
        if (!string.IsNullOrWhiteSpace(result.Data.MobileMask))
            return $"检测到安全手机验证，验证码将发送至 {result.Data.MobileMask}。";

        return "检测到安全手机验证，正在请求发送短信验证码…";
    }

    private static string BuildSmsInputPrompt(LoginResponse result)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(result.Data.SafePhoneTip))
            builder.AppendLine(result.Data.SafePhoneTip);
        else
            builder.AppendLine("当前登录需要通过安全手机短信完成验证。");

        if (!string.IsNullOrWhiteSpace(result.Data.MobileMask))
            builder.AppendLine($"验证码将发送至 {result.Data.MobileMask}。");

        builder.Append("请输入收到的短信验证码继续登录。");
        return builder.ToString();
    }

    private Task<LoginCaptchaChallenge> BuildStaticCaptchaPromptAsync(LoginResponse result) =>
        BuildStaticCaptchaPromptAsync(result, CancellationToken.None);

    private async Task<LoginCaptchaChallenge> BuildStaticCaptchaPromptAsync(LoginResponse result, CancellationToken cancellationToken)
    {
        var imageUrl   = GetCaptchaImageUrl(result);
        var imageBytes = imageUrl != null ? await context.DownloadCaptchaImageAsync(imageUrl, cancellationToken).ConfigureAwait(false) : null;
        var builder    = new StringBuilder("静态密码登录需要输入验证码。");

        if (!string.IsNullOrWhiteSpace(result.Data.FailReason))
            builder.Append($"\n提示：{result.Data.FailReason}");

        builder.Append("\n请输入图片中的验证码继续登录。");

        return new LoginCaptchaChallenge
        {
            Title      = "登录验证码",
            Prompt     = builder.ToString(),
            ImageBytes = imageBytes,
            RefreshAsync = imageUrl == null
                               ? null
                               : refreshCancellationToken => BuildStaticCaptchaPromptAsync(result, refreshCancellationToken)
        };
    }
}
