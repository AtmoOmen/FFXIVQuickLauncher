using XIVLauncher.Common.Game.Login;

namespace XIVLauncher.Accounts.DeviceProfiles;

public sealed class DeviceProfilePreset
{
    public string Id { get; init; } = string.Empty;

    public string Remark { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;

    public string MacAddress { get; init; } = string.Empty;

    public string HostName { get; init; } = string.Empty;

    public long GeneratedUtcTicks { get; init; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Remark)
            ? $"{HostName} / {MacAddress} / {GetShortDeviceId()}"
            : Remark;

    public bool Matches(DeviceProfileSnapshot snapshot) =>
        ToSnapshot().Equals(snapshot);

    public DeviceProfileSnapshot ToSnapshot() =>
        new()
        {
            DeviceId   = DeviceId,
            MacAddress = MacAddress,
            HostName   = HostName
        };

    public DeviceProfilePreset WithGeneratedUtcTicks(long generatedUtcTicks, string? remark = null) =>
        new()
        {
            Id                = Id,
            Remark            = remark?.Trim() ?? Remark,
            DeviceId          = DeviceId,
            MacAddress        = MacAddress,
            HostName          = HostName,
            GeneratedUtcTicks = generatedUtcTicks
        };

    private string GetShortDeviceId()
    {
        if (string.IsNullOrWhiteSpace(DeviceId))
            return "未命名";

        return DeviceId.Length <= 8
                   ? DeviceId
                   : DeviceId[..8];
    }
}
