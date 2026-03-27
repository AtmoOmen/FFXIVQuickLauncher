namespace XIVLauncher.Common.Game.Login;

public sealed class DeviceProfileSnapshot
{
    public required string DeviceId { get; init; }

    public required string MacAddress { get; init; }

    public required string HostName { get; init; }

    public string MacHash => FakeMachineInfo.GetMacHash(MacAddress);

    public string CasCid => FakeMachineInfo.GetCasCid(MacAddress);
}
