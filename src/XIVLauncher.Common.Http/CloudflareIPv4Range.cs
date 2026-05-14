using System.Net;

namespace XIVLauncher.Common.Http;

internal readonly struct CloudflareIPv4Range
{
    private readonly uint mask;
    private readonly uint network;

    public CloudflareIPv4Range(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();

        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        mask    = prefixLength == 0 ? 0 : uint.MaxValue << 32 - prefixLength;
        network = BitConverter.ToUInt32(bytes, 0) & mask;
    }

    public IPAddress GetCandidateAddress()
    {
        var candidate = network + 1;
        var bytes     = BitConverter.GetBytes(candidate);

        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        return new IPAddress(bytes);
    }
}
