using XIVLauncher.Common.Game.Login;

namespace XIVLauncher.Accounts;

public sealed record ResolvedDeviceProfile
(
    DeviceProfileSnapshot Snapshot,
    bool                  DynamicEnabled,
    bool                  IsRotationEnabled,
    int                   RotationDays,
    long                  LastGeneratedUtcTicks
);
