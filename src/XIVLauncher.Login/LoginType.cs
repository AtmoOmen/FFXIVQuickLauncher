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
    ///     WeGame 手动抓包登录
    /// </summary>
    WeGameManual,

    /// <summary>
    ///     WeGame 自动读取登录
    /// </summary>
    WeGameAuto,

    /// <summary>
    ///     自动登录
    /// </summary>
    AutoLoginSession
}

public static class LoginTypeExtensions
{
    public static XIVAccountType ToAccountType(this LoginType loginType) =>
        loginType switch
        {
            LoginType.WeGameAuto or LoginType.WeGameManual          => XIVAccountType.WeGame,
            LoginType.Static or LoginType.Slide or LoginType.QRCode => XIVAccountType.Sdo,
            _                                                       => throw new ArgumentOutOfRangeException(nameof(loginType), loginType, "未知登录类型")
        };
}
