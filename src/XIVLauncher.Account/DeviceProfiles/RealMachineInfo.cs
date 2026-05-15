using System.Net.NetworkInformation;

namespace XIVLauncher.Account.DeviceProfiles;

public static class RealMachineInfo
{
    public static DeviceProfileSnapshot CreateSnapshot()
    {
        var hostName   = GetHostName();
        var macAddress = GetMacAddress();

        return new DeviceProfileSnapshot
        {
            DeviceId   = FakeMachineInfo.CreateDeviceId(macAddress, hostName),
            MacAddress = macAddress,
            HostName   = hostName
        };
    }

    private static string GetHostName() =>
        Environment.MachineName.Trim().ToUpperInvariant();

    private static string GetMacAddress()
    {
        var     bestPriority   = int.MaxValue;
        byte[]? bestMacAddress = null;

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!TryGetMacAddress(networkInterface, out var macAddressBytes))
                continue;

            var priority = GetPriority(networkInterface);
            if (priority >= bestPriority)
                continue;

            bestPriority   = priority;
            bestMacAddress = macAddressBytes;
        }

        if (bestMacAddress == null)
            throw new InvalidOperationException("无法获取本机真实 MAC 地址");

        return FormatMacAddress(bestMacAddress);
    }

    private static bool TryGetMacAddress(NetworkInterface networkInterface, out byte[] macAddressBytes)
    {
        macAddressBytes = networkInterface.GetPhysicalAddress().GetAddressBytes();
        if (macAddressBytes.Length != 6)
            return false;

        if ((macAddressBytes[0] & 0x01) != 0)
            return false;

        var isAllZero = true;
        var isAllFF   = true;

        foreach (var macAddressByte in macAddressBytes)
        {
            if (macAddressByte != 0x00)
                isAllZero = false;

            if (macAddressByte != 0xFF)
                isAllFF = false;
        }

        return !isAllZero && !isAllFF;
    }

    private static int GetPriority(NetworkInterface networkInterface)
    {
        var priority = networkInterface.OperationalStatus == OperationalStatus.Up ? 0 : 100;

        priority += networkInterface.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet        => 0,
            NetworkInterfaceType.GigabitEthernet => 0,
            NetworkInterfaceType.FastEthernetFx  => 0,
            NetworkInterfaceType.FastEthernetT   => 0,
            NetworkInterfaceType.Wireless80211   => 10,
            NetworkInterfaceType.Loopback        => 1000,
            NetworkInterfaceType.Tunnel          => 1000,
            NetworkInterfaceType.Unknown         => 1000,
            _                                    => 50
        };

        var descriptor = $"{networkInterface.Name}|{networkInterface.Description}".ToUpperInvariant();
        if (descriptor.Contains("VIRTUAL",    StringComparison.Ordinal)
            || descriptor.Contains("VMWARE",  StringComparison.Ordinal)
            || descriptor.Contains("VBOX",    StringComparison.Ordinal)
            || descriptor.Contains("HYPER-V", StringComparison.Ordinal)
            || descriptor.Contains("TAP",     StringComparison.Ordinal)
            || descriptor.Contains("TUN",     StringComparison.Ordinal)
            || descriptor.Contains("VPN",     StringComparison.Ordinal))
            priority += 500;

        return priority;
    }

    private static string FormatMacAddress(byte[] macAddressBytes) =>
        string.Join("-", Array.ConvertAll(macAddressBytes, static macAddressByte => macAddressByte.ToString("X2")));
}
