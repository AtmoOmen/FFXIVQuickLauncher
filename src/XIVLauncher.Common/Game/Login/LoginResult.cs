using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.Game.Patch.V3;

namespace XIVLauncher.Common.Game.Login;

public class LoginResult
{
    public LoginState        State            { get; set; }
    public PatchListEntry[]? PendingPatches   { get; set; }
    public V3GameUpdatePlan? V3GameUpdatePlan { get; set; }
    public OAuthLoginResult? OAuthLogin       { get; set; }
    public string?           UniqueID         { get; set; }
    public LoginArea?        Area             { get; set; }
    public LoginArea[]?      Areas            { get; set; }
    public int               DCTravelPort     { get; set; }
}
