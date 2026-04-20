using System;

namespace XIVLauncher.Common.Game.Login;

public sealed class DeviceProfileSnapshot : IEquatable<DeviceProfileSnapshot>
{
    public required string DeviceId { get; init; }

    public required string MacAddress { get; init; }

    public required string HostName { get; init; }

    public string MacHash => FakeMachineInfo.GetMacHash(MacAddress);

    public string CasCid => FakeMachineInfo.GetCasCid(MacAddress);

    public bool Equals(DeviceProfileSnapshot? other) =>
        other is not null
        && string.Equals(DeviceId, other.DeviceId, StringComparison.Ordinal)
        && string.Equals(MacAddress, other.MacAddress, StringComparison.Ordinal)
        && string.Equals(HostName, other.HostName, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is DeviceProfileSnapshot other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(DeviceId, MacAddress, HostName);
}
