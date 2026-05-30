using XIVLauncher.Account.DeviceProfiles;

namespace XIVLauncher.Login;

public class OAuthLoginResult
{
    public string    InputUserID      { get; set; } = null!;
    public string    SndaID           { get; set; } = null!;
    public string    Password         { get; set; } = null!;
    public string?   QuickLoginSecret { get; set; }
    public int       Region           { get; set; }
    public bool      TermsAccepted    { get; set; }
    public bool      Playable         { get; set; }
    public int       MaxExpansion     { get; set; }
    public LoginType LoginType        { get; set; }
    
    // 游戏 session ticket，用于启动游戏时传递给 ffxiv_dx11.exe 的 DEV.TestSID 参数。
    // 登录阶段此字段为空，启动游戏前一刻才通过 TGT 实时获取。
    public string SessionID { get; set; } = null!;

    // CAS Ticket-Granting Ticket
    public string? TGT { get; set; }

    // CAS GUID
    public string? Guid { get; set; }

    public DeviceProfileSnapshot? DeviceProfile { get; set; }
}
