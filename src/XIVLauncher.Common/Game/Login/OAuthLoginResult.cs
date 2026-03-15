namespace XIVLauncher.Common.Game.Login;

public class OAuthLoginResult
{
    public string    SessionID           { get; set; } = null!;
    public string    InputUserID         { get; set; } = null!;
    public string    SndaID              { get; set; } = null!;
    public string    Password            { get; set; } = null!;
    public string?   AutoLoginSessionKey { get; set; }
    public int       Region              { get; set; }
    public bool      TermsAccepted       { get; set; }
    public bool      Playable            { get; set; }
    public int       MaxExpansion        { get; set; }
    public LoginType LoginType           { get; set; }
}
