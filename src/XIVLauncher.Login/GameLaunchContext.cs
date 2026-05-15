namespace XIVLauncher.Login;

public sealed class GameLaunchContext
(
    LoginResult loginResult,
    LoginArea   area,
    LoginArea[] areas
)
{
    public LoginResult LoginResult  { get; set; } = loginResult;
    public LoginArea   Area         { get; }      = area;
    public LoginArea[] Areas        { get; }      = areas;
    public int         DcTravelPort { get; set; }
}
