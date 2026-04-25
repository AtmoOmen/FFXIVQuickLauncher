using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace XIVLauncher.Common.Http;

internal readonly struct CloudflareIPV6Range
{
    private readonly ulong highMask;
    private readonly ulong highNetwork;
    private readonly ulong lowMask;
    private readonly ulong lowNetwork;

    public CloudflareIPV6Range(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var high  = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        var low   = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8));

        highMask    = prefixLength <= 0 ? 0 : prefixLength >= 64 ? ulong.MaxValue : ulong.MaxValue << 64 - prefixLength;
        lowMask     = prefixLength <= 64 ? 0 : prefixLength >= 128 ? ulong.MaxValue : ulong.MaxValue << 128 - prefixLength;
        highNetwork = high & highMask;
        lowNetwork  = low & lowMask;
    }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        var bytes = address.GetAddressBytes();
        var high  = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        var low   = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8));

        return (high & highMask) == highNetwork && (low & lowMask) == lowNetwork;
    }
}
