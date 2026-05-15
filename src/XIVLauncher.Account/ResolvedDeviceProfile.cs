using XIVLauncher.Account.DeviceProfiles;

namespace XIVLauncher.Account;

public sealed record ResolvedDeviceProfile
(
    DeviceProfileSnapshot Snapshot,
    string?               PresetId,
    bool                  DynamicEnabled,
    bool                  IsRotationEnabled,
    int                   RotationDays,
    long                  LastGeneratedUtcTicks
);
