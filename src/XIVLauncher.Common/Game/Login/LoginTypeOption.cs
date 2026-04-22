using System.Collections.Generic;

namespace XIVLauncher.Common.Game.Login;

public class LoginTypeOption
{
    public required LoginType      LoginType    { get; set; }
    public required LoginTypeGroup Group        { get; set; }
    public required string         DisplayName  { get; set; }
    public required string         GroupDisplay { get; set; }

    public static List<LoginTypeOption> Get()
    {
        var types = new List<LoginTypeOption>
        {
            new() { LoginType = LoginType.Slide, Group = LoginTypeGroup.Sdo, DisplayName = "一键登录", GroupDisplay = "盛趣渠道" },
            new() { LoginType = LoginType.QRCode, Group = LoginTypeGroup.Sdo, DisplayName = "扫码登录", GroupDisplay = "盛趣渠道" },
            new() { LoginType = LoginType.Static, Group = LoginTypeGroup.Sdo, DisplayName = "密码登录", GroupDisplay = "盛趣渠道" },
            new() { LoginType = LoginType.WeGameAuto, Group = LoginTypeGroup.WeGame, DisplayName = "自动读取登录", GroupDisplay = "WeGame 渠道" },
            new() { LoginType = LoginType.WeGameManual, Group = LoginTypeGroup.WeGame, DisplayName = "手动抓包登录", GroupDisplay = "WeGame 渠道" }
        };

        return types;
    }
}
