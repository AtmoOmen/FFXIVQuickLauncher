using XIVLauncher.Common.Game.Login;

namespace XIVLauncher.Accounts;

public sealed record ResolvedDeviceProfile
(
    DeviceProfileSnapshot      Snapshot,
    bool                       DynamicEnabled,
    DeviceProfileRotationMode  RotationMode,
    int                        RotationDays,
    long                       LastGeneratedUtcTicks
);
