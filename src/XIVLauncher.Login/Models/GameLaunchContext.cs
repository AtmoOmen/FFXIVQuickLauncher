using XIVLauncher.Common.Game;
using XIVLauncher.Login.Client;

namespace XIVLauncher.Login.Models;

public sealed class GameLaunchContext
(
    LoginResult    loginResult,
    LoginArea      area,
    LoginArea[]    areas,
    XIVAccountType accountType
)
{
    public LoginResult    LoginResult  { get; set; } = loginResult;
    public LoginArea      Area         { get; set; } = area;
    public LoginArea[]    Areas        { get; }      = areas;
    public XIVAccountType AccountType  { get; }      = accountType;
    public int            DcTravelPort { get; set; }
}
