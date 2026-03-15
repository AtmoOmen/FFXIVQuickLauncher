namespace XIVLauncher.Common.Game.Login;

public enum LoginType
{
    /// <summary>
    ///     静态密码登录
    /// </summary>
    Static,

    /// <summary>
    ///     手势登录
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
