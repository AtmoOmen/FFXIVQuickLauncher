using System.Collections.Generic;

namespace XIVLauncher.Common.Game.Login;

public class LoginTypeOption
{
    public required LoginType LoginType   { get; set; }
    public required string    DisplayName { get; set; }

    public static List<LoginTypeOption> Get(bool showWeGameToken = false)
    {
        var types = new List<LoginTypeOption>
        {
            new() { LoginType = LoginType.Slide, DisplayName  = "一键登录" },
            new() { LoginType = LoginType.QRCode, DisplayName = "扫码登录" },
            new() { LoginType = LoginType.Static, DisplayName = "密码登录" },
            new() { LoginType = LoginType.WeGameSID, DisplayName = "WeGame SID" }
        };

        if (showWeGameToken)
            types.Add(new() { LoginType = LoginType.WeGameToken, DisplayName = "WeGame 抓包" });

        return types;
    }
}
