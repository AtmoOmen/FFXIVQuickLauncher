using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace XIVLauncher.Common.Game.Login;

/// <summary>
///     生成登录请求所需的稳定匿名设备画像。
/// </summary>
public static class FakeMachineInfo
{
    private const string HostNameAlphabet                = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const byte   LocallyAdministeredUnicastOctet = 0x02;

    private static readonly SyntheticNicFamily[] SyntheticNicFamilies =
    [
        new(0x10, 0xA7),
        new(0x24, 0xC2),
        new(0x38, 0xD1),
        new(0x4C, 0x9E),
        new(0x60, 0xB4),
        new(0x74, 0xE8)
    ];

    private static readonly HostNamePattern[] HostNamePatterns =
    [
        new("desktop", "DESKTOP-", 7, 60),
        new("laptop", "LAPTOP-", 7, 35),
        new("mini-pc", "WIN-", 11, 5)
    ];

    public static DeviceProfileSnapshot CreateSnapshot()
    {
        Span<byte> machineSeed = stackalloc byte[16];
        RandomNumberGenerator.Fill(machineSeed);

        var context = CreateContext(machineSeed);
        return new DeviceProfileSnapshot
        {
            DeviceId   = CreateDeviceIdCore(context.MacAddress, context.HostName, context.ProfileKey),
            MacAddress = context.MacAddress,
            HostName   = context.HostName
        };
    }

    public static string GetMD5(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(MD5.HashData(payload));

    public static string GetMacHash(string macAddress) =>
        GetMD5(Encoding.ASCII.GetBytes(macAddress));

    public static string GetCasCid(string macAddress) =>
        $"CID{GetMacHash(macAddress)}";

    public static string CreateDeviceId() =>
        CreateSnapshot().DeviceId;

    public static string CreateDeviceId(string macAddress, string hostName)
    {
        var normalizedMacAddress = NormalizeMacAddress(macAddress);
        var normalizedHostName   = NormalizeHostName(hostName);
        var profileKey           = GetProfileKey(normalizedHostName);
        return CreateDeviceIdCore(normalizedMacAddress, normalizedHostName, profileKey);
    }

    public static string CreateMacAddress()
    {
        Span<byte> machineSeed = stackalloc byte[16];
        RandomNumberGenerator.Fill(machineSeed);
        return CreateContext(machineSeed).MacAddress;
    }

    public static string CreateHostName()
    {
        Span<byte> machineSeed = stackalloc byte[16];
        RandomNumberGenerator.Fill(machineSeed);
        return CreateContext(machineSeed).HostName;
    }

    private static SyntheticDeviceContext CreateContext(ReadOnlySpan<byte> machineSeed)
    {
        var generator  = new DeterministicSequence(machineSeed);
        var pattern    = SelectHostNamePattern(ref generator);
        var hostName   = CreateHostName(pattern, ref generator);
        var macAddress = CreateMacAddress(ref generator);

        return new SyntheticDeviceContext(pattern.ProfileKey, hostName, macAddress);
    }

    private static HostNamePattern SelectHostNamePattern(ref DeterministicSequence generator)
    {
        var roll          = generator.NextInt32(100);
        var cumulativeSum = 0;

        foreach (var pattern in HostNamePatterns)
        {
            cumulativeSum += pattern.Weight;
            if (roll < cumulativeSum)
                return pattern;
        }

        return HostNamePatterns[^1];
    }

    private static string CreateHostName(HostNamePattern pattern, ref DeterministicSequence generator)
    {
        Span<char> suffix = stackalloc char[pattern.SuffixLength];

        for (var i = 0; i < suffix.Length; i++)
            suffix[i] = HostNameAlphabet[generator.NextInt32(HostNameAlphabet.Length)];

        return string.Concat(pattern.Prefix, new string(suffix));
    }

    private static string CreateMacAddress(ref DeterministicSequence generator)
    {
        Span<byte> macBytes = stackalloc byte[6];
        macBytes[0] = LocallyAdministeredUnicastOctet;

        var family = SyntheticNicFamilies[generator.NextInt32(SyntheticNicFamilies.Length)];
        macBytes[1] = family.SecondByte;
        macBytes[2] = family.ThirdByte;
        macBytes[3] = generator.NextByte();
        macBytes[4] = generator.NextByte();
        macBytes[5] = generator.NextByte();

        var hex = Convert.ToHexString(macBytes);
        return $"{hex[..2]}-{hex[2..4]}-{hex[4..6]}-{hex[6..8]}-{hex[8..10]}-{hex[10..12]}";
    }

    private static string CreateDeviceIdCore(string macAddress, string hostName, string profileKey)
    {
        var macHash  = GetMacHash(macAddress);
        var payload  = $"v2|{profileKey}|{macAddress}|{hostName}";
        var cpuHash  = GetProfileHash("CPU",  payload);
        var diskHash = GetProfileHash("DISK", payload);
        return string.Join(":", macHash, cpuHash, diskHash);
    }

    private static string GetProfileHash(string category, string payload) =>
        GetMD5(Encoding.ASCII.GetBytes($"{category}|{payload}"));

    private static string GetProfileKey(string hostName)
    {
        foreach (var pattern in HostNamePatterns)
        {
            if (hostName.StartsWith(pattern.Prefix, StringComparison.Ordinal))
                return pattern.ProfileKey;
        }

        return "custom";
    }

    private static string NormalizeMacAddress(string macAddress)
    {
        var rawValue = macAddress.Trim().Replace(":", "-", StringComparison.Ordinal);
        var segments = rawValue.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length != 6)
            return rawValue.ToUpperInvariant();

        var normalizedSegments = new string[6];

        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length != 2 || !byte.TryParse(segments[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedByte))
                return rawValue.ToUpperInvariant();

            normalizedSegments[i] = parsedByte.ToString("X2", CultureInfo.InvariantCulture);
        }

        return string.Join("-", normalizedSegments);
    }

    private static string NormalizeHostName(string hostName) =>
        hostName.Trim().ToUpperInvariant();

    private readonly record struct HostNamePattern
    (
        string ProfileKey,
        string Prefix,
        int    SuffixLength,
        int    Weight
    );

    private readonly record struct SyntheticNicFamily
    (
        byte SecondByte,
        byte ThirdByte
    );

    private readonly record struct SyntheticDeviceContext
    (
        string ProfileKey,
        string HostName,
        string MacAddress
    );

    private struct DeterministicSequence
    (
        ReadOnlySpan<byte> seed
    )
    {
        private ulong state = CreateInitialState(seed);

        public int NextInt32(int exclusiveMax)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMax);
            return (int)(NextUInt64() % (uint)exclusiveMax);
        }

        public byte NextByte() =>
            (byte)NextUInt64();

        private ulong NextUInt64()
        {
            state += 0x9E3779B97F4A7C15UL;

            var value = state;
            value = (value ^ value >> 30) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ value >> 27) * 0x94D049BB133111EBUL;
            return value ^ value >> 31;
        }

        private static ulong CreateInitialState(ReadOnlySpan<byte> seed)
        {
            var firstHalf  = BinaryPrimitives.ReadUInt64LittleEndian(seed);
            var secondHalf = BinaryPrimitives.ReadUInt64LittleEndian(seed[8..]);
            var mixedState = firstHalf ^ secondHalf * 0x9E3779B97F4A7C15UL;
            return mixedState != 0 ? mixedState : 0xA5A5A5A5A5A5A5A5UL;
        }
    }
}
