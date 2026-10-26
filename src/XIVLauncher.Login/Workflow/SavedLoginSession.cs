using XIVLauncher.Account.DeviceProfiles;

namespace XIVLauncher.Login.Workflow;

internal sealed record SavedLoginSession
{
    public required string AccountID { get; init; }

    public required string SndaID { get; init; }

    public required string TGT { get; init; }

    public required string GUID { get; init; }

    public required DeviceProfileSnapshot DeviceProfile { get; init; }
}
