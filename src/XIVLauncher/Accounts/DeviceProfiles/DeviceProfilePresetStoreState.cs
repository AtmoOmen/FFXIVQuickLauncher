using System.Collections.Generic;

namespace XIVLauncher.Accounts.DeviceProfiles;

public sealed class DeviceProfilePresetStoreState
{
    public int Version { get; init; } = 1;

    public string SharedPresetId { get; init; } = string.Empty;

    public IReadOnlyList<DeviceProfilePreset> Presets { get; init; } = [];
}
