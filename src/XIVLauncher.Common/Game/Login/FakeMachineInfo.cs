using System;
using System.Security.Cryptography;
using System.Text;

namespace XIVLauncher.Common.Game.Login;

/// <summary>
///     生成登录请求所需的伪设备标识。
/// </summary>
public static class FakeMachineInfo
{
    private const string HOST_NAME_PREFIX   = "DESKTOP-";
    private const string HOST_NAME_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static DeviceProfileSnapshot CreateSnapshot() =>
        new()
        {
            DeviceId   = CreateDeviceId(),
            MacAddress = CreateMacAddress(),
            HostName   = CreateHostName()
        };

    public static string GetMD5(byte[] payload) =>
        Convert.ToHexString(MD5.HashData(payload));

    public static string GetMacHash(string macAddress) =>
        GetMD5(Encoding.ASCII.GetBytes(macAddress));

    public static string GetCasCid(string macAddress) =>
        $"CID{GetMacHash(macAddress)}";

    public static string CreateDeviceId() =>
        string.Join(":", CreateRandomMd5(), CreateRandomMd5(), CreateRandomMd5());

    public static string CreateMacAddress()
    {
        var macBytes = RandomNumberGenerator.GetBytes(6);

        macBytes[0] = (byte)((macBytes[0] | 0x02) & 0xFE);

        var hex = Convert.ToHexString(macBytes);
        return $"{hex[..2]}-{hex[2..4]}-{hex[4..6]}-{hex[6..8]}-{hex[8..10]}-{hex[10..12]}";
    }

    public static string CreateHostName()
    {
        Span<byte> randomBytes = stackalloc byte[7];
        Span<char> suffix      = stackalloc char[7];

        RandomNumberGenerator.Fill(randomBytes);

        for (var i = 0; i < suffix.Length; i++)
            suffix[i] = HOST_NAME_ALPHABET[randomBytes[i] % HOST_NAME_ALPHABET.Length];

        return string.Concat(HOST_NAME_PREFIX, new string(suffix));
    }

    private static string CreateRandomMd5()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return GetMD5(buffer.ToArray());
    }
}
