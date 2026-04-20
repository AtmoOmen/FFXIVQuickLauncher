using System;

namespace XIVLauncher.Windows;

internal sealed class AccountDeviceProfileExchangeData
{
    public int            Version                { get; init; }
    public string         AccountDisplayName     { get; init; } = string.Empty;
    public DateTimeOffset ExportedAtUtc          { get; init; }
    public bool           DynamicEnabled         { get; init; }
    public bool           PeriodicRefreshEnabled { get; init; }
    public int            RotationDays           { get; init; }
    public long           GeneratedUtcTicks      { get; init; }
    public string         PresetRemark           { get; init; } = string.Empty;
    public string         DeviceId               { get; init; } = string.Empty;
    public string         MacAddress             { get; init; } = string.Empty;
    public string         HostName               { get; init; } = string.Empty;
}
