namespace XIVLauncher.Login;

public enum LoginExceptionCode
{
    /// <summary>
    ///     该账号首次在本设备上登录，不支持一键登录，请使用二维码、动态密码或密码登录
    /// </summary>
    FirstLoginOnDevice = -10242296,

    /// <summary>
    ///     请您使用叨鱼APP扫码或一键方式登录
    /// </summary>
    UseDaoYuApp = -10386010,

    /// <summary>
    ///     登录环境存在风险，请使用叨鱼扫码登录，或通过安全手机发送短信验证
    /// </summary>
    /// <remarks>/authen/staticLogin.json</remarks>
    RiskEnvironment = -10386188,

    /// <summary>
    ///     动态密码错误（静态密码已锁定，请在【叨鱼】中设置静密锁）
    /// </summary>
    DynamicPasswordError = -10242226,

    /// <summary>
    ///     您的输入有误，请确认后重新输入
    /// </summary>
    /// <remarks>/authen/cancelPushMessageLogin.json</remarks>
    InvalidInput = -10242301,

    /// <summary>
    ///     二维码未通过验证，请重试
    /// </summary>
    /// <remarks>/authen/codeKeyLogin.json</remarks>
    QrCodeVerifyFailed = -10515805,

    /// <summary>
    ///     登录过期且未开启自动登录
    /// </summary>
    OutdatedLoginInfo = -10515004,

    /// <summary>
    ///     滑块验证超时或取消
    /// </summary>
    SlideTimeoutOrCanceled = 1,

    /// <summary>
    ///     静态登录需要验证码
    /// </summary>
    StaticNeedCaptcha = 2,

    /// <summary>
    ///     扫码获取账号信息失败
    /// </summary>
    ScanQrCodeGetAccountFail = 3,

    /// <summary>
    ///     扫码超时或取消
    /// </summary>
    ScanTimeoutOrCanceled = 4,

    /// <summary>
    ///     安全手机短信验证已取消
    /// </summary>
    SafePhoneVerificationCanceled = 5,

    /// <summary>
    ///     验证码输入已取消
    /// </summary>
    CaptchaVerificationCanceled = 6
}

[Serializable]
public class LoginException
(
    int    errorCode,
    string message,
    bool   removeQuickLoginSecret = false
) : Exception(message)
{
    public int ErrorCode = errorCode;

    public bool RemoveQuickLoginSecret = removeQuickLoginSecret;
}
