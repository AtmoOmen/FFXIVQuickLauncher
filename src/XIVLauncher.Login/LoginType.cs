using XIVLauncher.Common.Game;

namespace XIVLauncher.Login;

public enum LoginType
{
    /// <summary>
    ///     静态密码登录
    /// </summary>
    Static,

    /// <summary>
    ///     一键登录
    /// </summary>
    Slide,

    /// <summary>
    ///     扫码登录
    /// </summary>
    QRCode,

    /// <summary>
    ///     WeGame 登录
    /// </summary>
    WeGame,

    /// <summary>
    ///     快速登录会话
    /// </summary>
    QuickLogin
}

public static class LoginTypeExtensions
{
    public static XIVAccountType ToAccountType(this LoginType loginType) =>
        loginType switch
        {
            LoginType.WeGame                                        => XIVAccountType.WeGame,
            LoginType.Static or LoginType.Slide or LoginType.QRCode => XIVAccountType.Sdo,
            _                                                       => throw new ArgumentOutOfRangeException(nameof(loginType), loginType, "未知登录类型")
        };
}
