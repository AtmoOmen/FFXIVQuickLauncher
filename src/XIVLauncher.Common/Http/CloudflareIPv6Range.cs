using System.Buffers.Binary;
using System.Net;

namespace XIVLauncher.Common.Http;

internal readonly struct CloudflareIPv6Range
{
    private readonly ulong highNetwork;
    private readonly ulong lowNetwork;

    public CloudflareIPv6Range(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var high  = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        var low   = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8));

        var highMask = prefixLength <= 0  ? 0 : prefixLength >= 64  ? ulong.MaxValue : ulong.MaxValue << 64  - prefixLength;
        var lowMask  = prefixLength <= 64 ? 0 : prefixLength >= 128 ? ulong.MaxValue : ulong.MaxValue << 128 - prefixLength;

        highNetwork = high & highMask;
        lowNetwork  = low  & lowMask;
    }

    public IPAddress GetCandidateAddress()
    {
        Span<byte> bytes = stackalloc byte[16];

        BinaryPrimitives.WriteUInt64BigEndian(bytes,      highNetwork);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], lowNetwork + 1);

        return new IPAddress(bytes);
    }
}
