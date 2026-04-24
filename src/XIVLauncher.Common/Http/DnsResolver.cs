using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace XIVLauncher.Common.Http;

internal static class DNSResolver
{
    private static readonly ConcurrentDictionary<string, CacheEntry> CachedCandidatesByHost =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly CloudflareIpv4Range[] CloudflareRanges =
    [
        new(IPAddress.Parse("173.245.48.0"), 20),
        new(IPAddress.Parse("103.21.244.0"), 22),
        new(IPAddress.Parse("103.22.200.0"), 22),
        new(IPAddress.Parse("103.31.4.0"), 22),
        new(IPAddress.Parse("141.101.64.0"), 18),
        new(IPAddress.Parse("108.162.192.0"), 18),
        new(IPAddress.Parse("190.93.240.0"), 20),
        new(IPAddress.Parse("188.114.96.0"), 20),
        new(IPAddress.Parse("197.234.240.0"), 22),
        new(IPAddress.Parse("198.41.128.0"), 17),
        new(IPAddress.Parse("162.158.0.0"), 15),
        new(IPAddress.Parse("104.16.0.0"), 13),
        new(IPAddress.Parse("104.24.0.0"), 14),
        new(IPAddress.Parse("172.64.0.0"), 13),
        new(IPAddress.Parse("131.0.72.0"), 22)
    ];

    private static readonly ConcurrentDictionary<string, Task<CacheEntry>> PendingResolutionsByHost =
        new(StringComparer.OrdinalIgnoreCase);

    private static HijackCacheEntry? hijackCacheEntry;

    private static Task<HijackCacheEntry>? pendingHijackResolution;

    public static async Task<IReadOnlyList<ConnectionCandidate>> GetSortedAddressesAsync(string hostname, CancellationToken token)
    {
        if (TryGetCachedCandidates(hostname, out var cachedCandidates))
            return cachedCandidates;

        var task = PendingResolutionsByHost.GetOrAdd(hostname, static host => ResolveCandidatesAsync(host));

        try
        {
            var cacheEntry = await task.WaitAsync(token).ConfigureAwait(false);
            CachedCandidatesByHost[hostname] = cacheEntry;
            return cacheEntry.Candidates;
        }
        finally
        {
            if (task.IsCompleted)
                PendingResolutionsByHost.TryRemove(hostname, out _);
        }
    }

    private static void AppendCandidates
    (
        List<ConnectionCandidate>  candidates,
        HashSet<IPAddress>         seenAddresses,
        IReadOnlyList<IPAddress>   addresses,
        ConnectionCandidateSource  source
    )
    {
        foreach (var address in addresses)
        {
            if (!seenAddresses.Add(address))
                continue;

            candidates.Add(new ConnectionCandidate(address, source, candidates.Count));
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> GetHijackAddressesAsync()
    {
        var snapshot = hijackCacheEntry;

        if (snapshot is { } cachedEntry && cachedEntry.ExpiresAt > DateTimeOffset.UtcNow)
            return cachedEntry.Addresses;

        var pendingTask = pendingHijackResolution;

        if (pendingTask == null || pendingTask.IsCompleted)
        {
            var createdTask = ResolveHijackAddressesAsync();
            var originalTask = Interlocked.CompareExchange(ref pendingHijackResolution, createdTask, pendingTask);
            pendingTask = originalTask ?? createdTask;
        }

        try
        {
            var resolvedEntry = await pendingTask.ConfigureAwait(false);
            hijackCacheEntry = resolvedEntry;
            return resolvedEntry.Addresses;
        }
        finally
        {
            if (pendingTask.IsCompleted)
                _ = Interlocked.CompareExchange(ref pendingHijackResolution, null, pendingTask);
        }
    }

    private static bool IsCloudflareIp(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        for (var i = 0; i < CloudflareRanges.Length; i++)
        {
            if (CloudflareRanges[i].Contains(address))
                return true;
        }

        return false;
    }

    private static async Task<CacheEntry> ResolveCandidatesAsync(string hostname)
    {
        var directAddresses = await ResolveIpv4AddressesAsync(hostname).ConfigureAwait(false);

        if (directAddresses.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        var candidates    = new List<ConnectionCandidate>(directAddresses.Count + MAX_HIJACK_ADDRESS_COUNT);
        var seenAddresses = new HashSet<IPAddress>();

        if (hostname.Equals(HIJACK_TARGET_HOST, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                AppendCandidates(candidates, seenAddresses, directAddresses, ConnectionCandidateSource.DirectDns);

                var hijackAddresses = await GetHijackAddressesAsync().ConfigureAwait(false);

                if (hijackAddresses.Count > 0)
                    AppendCandidates(candidates, seenAddresses, hijackAddresses, ConnectionCandidateSource.HijackDns);
                else
                    Log.Warning("Cloudflare 劫持入口 {HijackCname} 未返回可用 IPv4 地址", HIJACK_CNAME);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "解析 Cloudflare 劫持入口 {HijackCname} 失败", HIJACK_CNAME);
            }
        }
        else
        {
            AppendCandidates(candidates, seenAddresses, directAddresses, ConnectionCandidateSource.DirectDns);
        }

        var orderedCandidates = ConnectionTelemetryStore.SortCandidates(hostname, candidates);

        return new CacheEntry(orderedCandidates, DateTimeOffset.UtcNow.AddSeconds(HOST_CACHE_SECONDS));
    }

    private static async Task<HijackCacheEntry> ResolveHijackAddressesAsync()
    {
        var resolvedAddresses = await ResolveIpv4AddressesAsync(HIJACK_CNAME).ConfigureAwait(false);
        var filteredAddresses = new List<IPAddress>(Math.Min(resolvedAddresses.Count, MAX_HIJACK_ADDRESS_COUNT));

        for (var i = 0; i < resolvedAddresses.Count && filteredAddresses.Count < MAX_HIJACK_ADDRESS_COUNT; i++)
        {
            var address = resolvedAddresses[i];

            if (IsCloudflareIp(address))
                filteredAddresses.Add(address);
        }

        return new HijackCacheEntry(filteredAddresses, DateTimeOffset.UtcNow.AddSeconds(HIJACK_CACHE_SECONDS));
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveIpv4AddressesAsync(string hostname)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(DNS_RESOLVE_TIMEOUT_SECONDS));
        var resolvedAddresses = await Dns.GetHostAddressesAsync(hostname, AddressFamily.InterNetwork, cts.Token).ConfigureAwait(false);

        if (resolvedAddresses.Length == 0)
            return [];

        var uniqueAddresses = new HashSet<IPAddress>();
        var orderedAddresses = new List<IPAddress>(resolvedAddresses.Length);

        foreach (var address in resolvedAddresses)
        {
            if (!uniqueAddresses.Add(address))
                continue;

            orderedAddresses.Add(address);
        }

        return orderedAddresses;
    }

    private static bool TryGetCachedCandidates(string hostname, out IReadOnlyList<ConnectionCandidate> candidates)
    {
        if (CachedCandidatesByHost.TryGetValue(hostname, out var cacheEntry) && cacheEntry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            candidates = cacheEntry.Candidates;
            return true;
        }

        CachedCandidatesByHost.TryRemove(hostname, out _);
        candidates = [];
        return false;
    }

    private readonly record struct CacheEntry
    (
        IReadOnlyList<ConnectionCandidate> Candidates,
        DateTimeOffset                     ExpiresAt
    );

    private readonly record struct HijackCacheEntry
    (
        IReadOnlyList<IPAddress> Addresses,
        DateTimeOffset           ExpiresAt
    );

    #region Constants

    private const int DNS_RESOLVE_TIMEOUT_SECONDS = 4;

    private const int HIJACK_CACHE_SECONDS = 90;

    private const string HIJACK_CNAME = "cf.951886.xyz";

    private const string HIJACK_TARGET_HOST = "gh.atmoomen.top";

    private const int HOST_CACHE_SECONDS = 75;

    private const int MAX_HIJACK_ADDRESS_COUNT = 4;

    #endregion
}
