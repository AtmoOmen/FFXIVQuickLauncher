using System;
using System.Net;

namespace XIVLauncher.Common.Http;

internal readonly struct CloudflareIpv4Range
{
    private readonly uint mask;
    private readonly uint network;

    public CloudflareIpv4Range(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();

        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        mask    = prefixLength == 0 ? 0 : uint.MaxValue << 32 - prefixLength;
        network = BitConverter.ToUInt32(bytes, 0) & mask;
    }

    public bool Contains(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        return (BitConverter.ToUInt32(bytes, 0) & mask) == network;
    }
}
