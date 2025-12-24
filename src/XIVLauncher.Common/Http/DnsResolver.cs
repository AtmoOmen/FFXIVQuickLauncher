using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace XIVLauncher.Common.Http;

public static class DnsResolver
{
    private const string HijackCname = "cf.951886.xyz";

    private const AddressFamily ForcedAddressFamily = AddressFamily.InterNetwork;

    private static readonly List<CidrRange> CachedRanges;

    // From https://www.cloudflare.com/ips/
    static DnsResolver()
    {
        var rawRanges = new List<(string Ip, int Prefix)>
        {
            ("173.245.48.0", 20),
            ("103.21.244.0", 22),
            ("103.22.200.0", 22),
            ("103.31.4.0", 22),
            ("141.101.64.0", 18),
            ("108.162.192.0", 18),
            ("190.93.240.0", 20),
            ("188.114.96.0", 20),
            ("197.234.240.0", 22),
            ("198.41.128.0", 17),
            ("162.158.0.0", 15),
            ("104.16.0.0", 13),
            ("104.24.0.0", 14),
            ("172.64.0.0", 13),
            ("131.0.72.0", 22)
        };

        CachedRanges = new List<CidrRange>(rawRanges.Count);

        foreach (var (ipStr, prefix) in rawRanges)
        {
            CachedRanges.Add(new CidrRange(IPAddress.Parse(ipStr), prefix));
        }
    }

    private static bool IsCloudflareIp(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = ip.GetAddressBytes();

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        var ipNum = BitConverter.ToUInt32(bytes, 0);

        return CachedRanges.Any(range => (ipNum & range.Mask) == range.Network);
    }

    public static async Task<List<IPAddress>> GetSortedAddressesAsync(string hostname, CancellationToken token)
    {
        var dnsRecords = await Dns.GetHostAddressesAsync(hostname, ForcedAddressFamily, token);

        if (dnsRecords.Length > 0 &&
            !string.IsNullOrEmpty(HijackCname) &&
            dnsRecords.All(IsCloudflareIp))
        {
            try
            {
                var cnameRecords = await Dns.GetHostAddressesAsync(HijackCname, ForcedAddressFamily, token);
                var selectedCnameIps = cnameRecords
                                       .Where(IsCloudflareIp)
                                       .Take(3)
                                       .ToArray();

                if (selectedCnameIps.Length > 0)
                {
                    dnsRecords = selectedCnameIps
                                 .Concat(dnsRecords)
                                 .Distinct()
                                 .ToArray();
                }
                else
                {
                    Log.Warning("CNAME {_hijackCname} resolved to empty or invalid IPs for {Hostname}", HijackCname,
                                hostname);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to resolve CNAME {_hijackCname} for hijack of {Hostname}", HijackCname,
                            hostname);
            }
        }

        var groups = dnsRecords
                     .GroupBy(a => a.AddressFamily)
                     .Select(g => g.ToArray())
                     .ToArray<IEnumerable<IPAddress>>();
        return ZipperMerge(groups).ToList();
    }

    private readonly struct CidrRange
    {
        public readonly uint Network;
        public readonly uint Mask;

        public CidrRange(IPAddress ip, int prefixLength)
        {
            var bytes = ip.GetAddressBytes();
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);

            var ipNum = BitConverter.ToUInt32(bytes, 0);
            var mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);

            Network = ipNum & mask;
            Mask = mask;
        }
    }

    /// <summary>
    /// Perform a "zipper merge" (A, 1, B, 2, C, 3) of multiple enumerables, allowing for lists to end early.
    /// Copied From https://github.com/goatcorp/Dalamud/blob/3be14d4135fecf3816d033e87259115941b18494/Dalamud/Utility/Util.cs#L502-L544
    /// </summary>
    /// <param name="sources">A set of enumerable sources to combine.</param>
    /// <typeparam name="TSource">The resulting type of the merged list to return.</typeparam>
    /// <returns>A new enumerable, consisting of the final merge of all lists.</returns>
    public static IEnumerable<TSource> ZipperMerge<TSource>(params IEnumerable<TSource>[] sources)
    {
        // Borrowed from https://codereview.stackexchange.com/a/263451, thank you!
        var enumerators = new IEnumerator<TSource>[sources.Length];

        try
        {
            for (var i = 0; i < sources.Length; i++)
            {
                enumerators[i] = sources[i].GetEnumerator();
            }

            var hasNext = new bool[enumerators.Length];

            bool MoveNext()
            {
                var anyHasNext = false;

                for (var i = 0; i < enumerators.Length; i++)
                {
                    anyHasNext |= hasNext[i] = enumerators[i].MoveNext();
                }

                return anyHasNext;
            }

            while (MoveNext())
            {
                for (var i = 0; i < enumerators.Length; i++)
                {
                    if (hasNext[i])
                    {
                        yield return enumerators[i].Current;
                    }
                }
            }
        }
        finally
        {
            foreach (var enumerator in enumerators)
            {
                enumerator?.Dispose();
            }
        }
    }
}
