using System;
using System.Security.Cryptography;
using System.Text;

namespace XIVLauncher.Common.Game.Login;

/// <summary>
///     生成登录请求所需的伪设备标识。
/// </summary>
public static class FakeMachineInfo
{
    private const string HOST_NAME_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private static readonly byte[][] CommonVendorOuiPool =
    [
        [0x00, 0x1B, 0x21],
        [0x3C, 0x52, 0x82],
        [0x40, 0xB0, 0x76],
        [0x58, 0x11, 0x22],
        [0x8C, 0x16, 0x45],
        [0x98, 0x90, 0x96],
        [0xA4, 0xBB, 0x6D],
        [0xB8, 0xAE, 0xED],
        [0xD8, 0x5D, 0x4C],
        [0xF0, 0x18, 0x98]
    ];

    private static readonly (string Prefix, int SuffixLength)[] HostNamePatterns =
    [
        ("DESKTOP-", 7),
        ("LAPTOP-", 7),
        ("WIN-", 11)
    ];

    public static DeviceProfileSnapshot CreateSnapshot()
    {
        Span<byte> machineSeed = stackalloc byte[16];
        RandomNumberGenerator.Fill(machineSeed);

        var macAddress = CreateMacAddress();
        var hostName   = CreateHostName(machineSeed);

        return new DeviceProfileSnapshot
        {
            DeviceId   = CreateDeviceId(macAddress, hostName, machineSeed),
            MacAddress = macAddress,
            HostName   = hostName
        };
    }

    public static string GetMD5(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(MD5.HashData(payload));

    public static string GetMacHash(string macAddress) =>
        GetMD5(Encoding.ASCII.GetBytes(macAddress));

    public static string GetCasCid(string macAddress) =>
        $"CID{GetMacHash(macAddress)}";

    public static string CreateDeviceId()
    {
        Span<byte> machineSeed = stackalloc byte[16];
        RandomNumberGenerator.Fill(machineSeed);

        var macAddress = CreateMacAddress();
        var hostName   = CreateHostName(machineSeed);
        return CreateDeviceId(macAddress, hostName, machineSeed);
    }

    public static string CreateDeviceId(string macAddress, string hostName)
    {
        Span<byte> machineSeed = stackalloc byte[16];
        RandomNumberGenerator.Fill(machineSeed);
        return CreateDeviceId(macAddress, hostName, machineSeed);
    }

    public static string CreateMacAddress()
    {
        var ouiIndex = RandomNumberGenerator.GetInt32(CommonVendorOuiPool.Length);
        var macBytes = RandomNumberGenerator.GetBytes(6);
        CommonVendorOuiPool[ouiIndex].CopyTo(macBytes, 0);

        macBytes[0] &= 0xFE;

        var hex = Convert.ToHexString(macBytes);
        return $"{hex[..2]}-{hex[2..4]}-{hex[4..6]}-{hex[6..8]}-{hex[8..10]}-{hex[10..12]}";
    }

    public static string CreateHostName()
    {
        Span<byte> machineSeed = stackalloc byte[16];
        RandomNumberGenerator.Fill(machineSeed);
        return CreateHostName(machineSeed);
    }

    private static string CreateDeviceId(string macAddress, string hostName, ReadOnlySpan<byte> machineSeed)
    {
        var macHash  = GetMacHash(macAddress);
        var cpuHash  = GetProfileHash("CPU",  macAddress, hostName, machineSeed);
        var diskHash = GetProfileHash("DISK", macAddress, hostName, machineSeed);
        return string.Join(":", macHash, cpuHash, diskHash);
    }

    private static string CreateHostName(ReadOnlySpan<byte> machineSeed)
    {
        var pattern = HostNamePatterns[machineSeed[0] % HostNamePatterns.Length];
        Span<char> suffix = stackalloc char[pattern.SuffixLength];

        for (var i = 0; i < suffix.Length; i++)
            suffix[i] = HOST_NAME_ALPHABET[machineSeed[i + 1] % HOST_NAME_ALPHABET.Length];

        return string.Concat(pattern.Prefix, new string(suffix));
    }

    private static string CreateRandomMd5()
    {
        var payload = Encoding.ASCII.GetBytes($"{category}|{macAddress}|{hostName}|{Convert.ToHexString(machineSeed)}");
        return GetMD5(payload);
    }
}
