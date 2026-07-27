using XIVLauncher.Login.Models;

namespace XIVLauncher.Login.Client;

public class LoginResult
{
    public LoginState        State      { get; set; }
    public OAuthLoginResult? OAuthLogin { get; set; }
    public string?           UniqueID   { get; set; }
}
