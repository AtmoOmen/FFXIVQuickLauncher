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

    private static readonly CloudflareIPV4Range[] CloudflareIpv4Ranges =
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

    private static readonly CloudflareIPV6Range[] CloudflareIpv6Ranges =
    [
        new(IPAddress.Parse("2400:cb00::"), 32),
        new(IPAddress.Parse("2606:4700::"), 32),
        new(IPAddress.Parse("2803:f800::"), 32),
        new(IPAddress.Parse("2405:b500::"), 32),
        new(IPAddress.Parse("2405:8100::"), 32),
        new(IPAddress.Parse("2a06:98c0::"), 29),
        new(IPAddress.Parse("2c0f:f248::"), 32)
    ];

    private static readonly ConcurrentDictionary<string, Task<CacheEntry>> PendingResolutionsByHost =
        new(StringComparer.OrdinalIgnoreCase);

    private static HijackCacheEntry? hijackCacheEntry;

    private static Task<HijackCacheEntry>? pendingHijackResolution;

    public static async Task<IReadOnlyList<ConnectionCandidate>> GetSortedAddressesAsync(string hostname, AddressFamily addressFamily, CancellationToken token)
    {
        if (TryGetCachedCandidates(hostname, addressFamily, out var cachedCandidates))
            return cachedCandidates;

        var task = PendingResolutionsByHost.GetOrAdd(hostname, static host => ResolveCandidatesAsync(host));

        try
        {
            var cacheEntry = await task.WaitAsync(token).ConfigureAwait(false);
            CachedCandidatesByHost[hostname] = cacheEntry;
            return FilterCandidates(cacheEntry.Candidates, addressFamily);
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

    private static IReadOnlyList<ConnectionCandidate> FilterCandidates(IReadOnlyList<ConnectionCandidate> candidates, AddressFamily addressFamily)
    {
        if (addressFamily != AddressFamily.InterNetwork && addressFamily != AddressFamily.InterNetworkV6)
            return candidates;

        var filteredCandidates = new List<ConnectionCandidate>(candidates.Count);

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];

            if (candidate.Address.AddressFamily == addressFamily)
                filteredCandidates.Add(candidate);
        }

        return filteredCandidates;
    }

    private static bool IsCloudflareIp(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            for (var i = 0; i < CloudflareIpv4Ranges.Length; i++)
            {
                if (CloudflareIpv4Ranges[i].Contains(address))
                    return true;
            }

            return false;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        for (var i = 0; i < CloudflareIpv6Ranges.Length; i++)
        {
            if (CloudflareIpv6Ranges[i].Contains(address))
                return true;
        }

        return false;
    }

    private static async Task<CacheEntry> ResolveCandidatesAsync(string hostname)
    {
        var hijackAddressesTask = hostname.Equals(HIJACK_TARGET_HOST, StringComparison.OrdinalIgnoreCase) ? GetHijackAddressesAsync() : null;
        var directAddressesTask = ResolveAddressesAsync(hostname);
        var cacheSeconds        = HOST_CACHE_SECONDS;
        IReadOnlyList<IPAddress> directAddresses;

        try
        {
            directAddresses = hijackAddressesTask == null
                                  ? await directAddressesTask.ConfigureAwait(false)
                                  : await directAddressesTask.WaitAsync(TimeSpan.FromMilliseconds(DIRECT_RESOLVE_WAIT_MS)).ConfigureAwait(false);
        }
        catch (TimeoutException) when (hijackAddressesTask != null)
        {
            cacheSeconds = DNS_PENDING_HOST_CACHE_SECONDS;

            _ = directAddressesTask.ContinueWith
            (
                static completedTask =>
                {
                    if (completedTask.Exception is { } exception)
                        Log.Verbose(exception.Flatten(), "{Host} 直连地址后台解析失败", HIJACK_TARGET_HOST);
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );

            Log.Verbose("{Host} 直连地址解析仍在进行, 先尝试 Cloudflare 劫持入口", hostname);

            if (await Task.WhenAny(directAddressesTask, hijackAddressesTask).ConfigureAwait(false) == directAddressesTask)
            {
                try
                {
                    directAddresses = await directAddressesTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "解析 {Host} 直连地址失败, 尝试 Cloudflare 劫持入口", hostname);
                    directAddresses = [];
                }
            }
            else
            {
                if (hijackAddressesTask.IsCompletedSuccessfully)
                {
                    directAddresses = [];
                }
                else
                {
                    try
                    {
                        directAddresses = await directAddressesTask.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "解析 {Host} 直连地址失败, 尝试 Cloudflare 劫持入口", hostname);
                        directAddresses = [];
                    }
                }
            }
        }
        catch (Exception ex) when (hijackAddressesTask != null)
        {
            Log.Warning(ex, "解析 {Host} 直连地址失败, 尝试 Cloudflare 劫持入口", hostname);
            directAddresses = [];
        }

        if (directAddresses.Count == 0 && hijackAddressesTask == null)
            throw new SocketException((int)SocketError.HostNotFound);

        var candidates    = new List<ConnectionCandidate>(directAddresses.Count + MAX_HIJACK_ADDRESS_COUNT);
        var seenAddresses = new HashSet<IPAddress>();

        if (directAddresses.Count > 0)
            AppendCandidates(candidates, seenAddresses, directAddresses, ConnectionCandidateSource.DirectDns);

        if (hijackAddressesTask != null)
        {
            try
            {
                var hijackAddresses = directAddresses.Count == 0
                                          ? await hijackAddressesTask.ConfigureAwait(false)
                                          : await hijackAddressesTask.WaitAsync(TimeSpan.FromMilliseconds(HIJACK_RESOLVE_WAIT_MS)).ConfigureAwait(false);

                if (hijackAddresses.Count > 0)
                    AppendCandidates(candidates, seenAddresses, hijackAddresses, ConnectionCandidateSource.HijackDns);
                else
                    Log.Warning("Cloudflare 劫持入口 {HijackCname} 未返回可用 IP 地址", HIJACK_CNAME);
            }
            catch (TimeoutException)
            {
                cacheSeconds = HIJACK_PENDING_HOST_CACHE_SECONDS;

                _ = hijackAddressesTask.ContinueWith
                (
                    static completedTask =>
                    {
                        if (completedTask.Exception is { } exception)
                            Log.Verbose(exception.Flatten(), "Cloudflare 劫持入口 {HijackCname} 后台解析失败", HIJACK_CNAME);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );

                Log.Verbose("Cloudflare 劫持入口 {HijackCname} 解析仍在进行, 先使用直连候选", HIJACK_CNAME);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "解析 Cloudflare 劫持入口 {HijackCname} 失败", HIJACK_CNAME);
            }
        }

        if (candidates.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        var orderedCandidates = ConnectionTelemetryStore.SortCandidates(hostname, candidates);

        return new CacheEntry(orderedCandidates, DateTimeOffset.UtcNow.AddSeconds(cacheSeconds));
    }

    private static async Task<HijackCacheEntry> ResolveHijackAddressesAsync()
    {
        var resolvedAddresses = await ResolveAddressesAsync(HIJACK_CNAME).ConfigureAwait(false);
        var ipv4Addresses     = new List<IPAddress>(Math.Min(resolvedAddresses.Count, MAX_HIJACK_ADDRESS_COUNT));
        var ipv6Addresses     = new List<IPAddress>(Math.Min(resolvedAddresses.Count, MAX_HIJACK_ADDRESS_COUNT));

        for (var i = 0; i < resolvedAddresses.Count; i++)
        {
            var address = resolvedAddresses[i];

            if (!IsCloudflareIp(address))
                continue;

            if (address.AddressFamily == AddressFamily.InterNetwork && ipv4Addresses.Count < MAX_HIJACK_ADDRESS_COUNT)
                ipv4Addresses.Add(address);
            else if (address.AddressFamily == AddressFamily.InterNetworkV6 && ipv6Addresses.Count < MAX_HIJACK_ADDRESS_COUNT)
                ipv6Addresses.Add(address);
        }

        var filteredAddresses = new List<IPAddress>(Math.Min(ipv4Addresses.Count + ipv6Addresses.Count, MAX_HIJACK_ADDRESS_COUNT));

        for (var i = 0; filteredAddresses.Count < MAX_HIJACK_ADDRESS_COUNT && (i < ipv4Addresses.Count || i < ipv6Addresses.Count); i++)
        {
            if (i < ipv4Addresses.Count)
                filteredAddresses.Add(ipv4Addresses[i]);

            if (filteredAddresses.Count < MAX_HIJACK_ADDRESS_COUNT && i < ipv6Addresses.Count)
                filteredAddresses.Add(ipv6Addresses[i]);
        }

        return new HijackCacheEntry(filteredAddresses, DateTimeOffset.UtcNow.AddSeconds(HIJACK_CACHE_SECONDS));
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAddressesAsync(string hostname)
    {
        if (IPAddress.TryParse(hostname, out var parsedAddress))
            return parsedAddress.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 ? [parsedAddress] : [];

        if (hostname.Equals("localhost", StringComparison.OrdinalIgnoreCase) || hostname.Equals("localhost.", StringComparison.OrdinalIgnoreCase))
            return [IPAddress.Loopback, IPAddress.IPv6Loopback];

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(DNS_RESOLVE_TIMEOUT_SECONDS));
        var resolvedAddresses = await Dns.GetHostAddressesAsync(hostname, cts.Token).ConfigureAwait(false);

        if (resolvedAddresses.Length == 0)
            return [];

        var uniqueAddresses = new HashSet<IPAddress>();
        var orderedAddresses = new List<IPAddress>(resolvedAddresses.Length);

        foreach (var address in resolvedAddresses)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork && address.AddressFamily != AddressFamily.InterNetworkV6)
                continue;

            if (!uniqueAddresses.Add(address))
                continue;

            orderedAddresses.Add(address);
        }

        return orderedAddresses;
    }

    private static bool TryGetCachedCandidates(string hostname, AddressFamily addressFamily, out IReadOnlyList<ConnectionCandidate> candidates)
    {
        if (CachedCandidatesByHost.TryGetValue(hostname, out var cacheEntry) && cacheEntry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            candidates = FilterCandidates(cacheEntry.Candidates, addressFamily);
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

    private const int DIRECT_RESOLVE_WAIT_MS = 650;

    private const int DNS_PENDING_HOST_CACHE_SECONDS = 8;

    private const int HIJACK_CACHE_SECONDS = 90;

    private const string HIJACK_CNAME = "cf.951886.xyz";

    private const int HIJACK_PENDING_HOST_CACHE_SECONDS = 8;

    private const int HIJACK_RESOLVE_WAIT_MS = 450;

    private const string HIJACK_TARGET_HOST = "gh.atmoomen.top";

    private const int HOST_CACHE_SECONDS = 75;

    private const int MAX_HIJACK_ADDRESS_COUNT = 6;

    #endregion
}
