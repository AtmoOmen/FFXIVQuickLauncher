using System;

namespace XIVLauncher.Common.Game.Login;

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
    ///     WeGame 抓包登录
    /// </summary>
    WeGameToken,

    /// <summary>
    ///     WeGame SID 登录
    /// </summary>
    WeGameSID,

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
            LoginType.WeGameSID                                     => XIVAccountType.WeGameSID,
            LoginType.WeGameToken                                   => XIVAccountType.WeGame,
            LoginType.Static or LoginType.Slide or LoginType.QRCode => XIVAccountType.Sdo,
            _                                                       => throw new ArgumentOutOfRangeException(nameof(loginType), loginType, "未知登录类型")
        };
}
