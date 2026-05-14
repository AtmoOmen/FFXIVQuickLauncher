using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;
using Serilog.Events;

namespace XIVLauncher.Common.Http;

internal static class DNSResolver
{
    private static readonly ConcurrentDictionary<string, CacheEntry> CachedCandidatesByHost =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly CloudflareIPv4Range[] CloudflareIPv4Ranges =
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

    private static readonly CloudflareIPv6Range[] CloudflareIPv6Ranges =
    [
        new(IPAddress.Parse("2400:cb00::"), 32),
        new(IPAddress.Parse("2606:4700::"), 32),
        new(IPAddress.Parse("2803:f800::"), 32),
        new(IPAddress.Parse("2405:b500::"), 32),
        new(IPAddress.Parse("2405:8100::"), 32),
        new(IPAddress.Parse("2a06:98c0::"), 29),
        new(IPAddress.Parse("2c0f:f248::"), 32)
    ];

    private static readonly IReadOnlyList<IPAddress> CloudflareCandidateAddresses = CreateCloudflareCandidateAddresses();

    private static readonly ConcurrentDictionary<string, string> LastLoggedCandidateSignaturesByHost =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Task<CacheEntry>> PendingResolutionsByHost =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<ConnectionCandidate>> GetSortedAddressesAsync(string hostname, AddressFamily addressFamily, CancellationToken token)
    {
        if (TryGetCachedCandidates(hostname, out var cachedCandidates))
            return PrepareCandidates(hostname, cachedCandidates, addressFamily);

        var task = PendingResolutionsByHost.GetOrAdd(hostname, static host => ResolveCandidatesAsync(host));

        try
        {
            var cacheEntry = await task.WaitAsync(token).ConfigureAwait(false);
            CachedCandidatesByHost[hostname] = cacheEntry;
            return PrepareCandidates(hostname, cacheEntry.Candidates, addressFamily);
        }
        finally
        {
            if (task.IsCompleted)
                PendingResolutionsByHost.TryRemove(hostname, out _);
        }
    }

    private static void AppendCandidates
    (
        List<ConnectionCandidate> candidates,
        HashSet<IPAddress>        seenAddresses,
        IReadOnlyList<IPAddress>  addresses,
        ConnectionCandidateSource source
    )
    {
        foreach (var address in addresses)
        {
            if (!seenAddresses.Add(address))
                continue;

            candidates.Add(new ConnectionCandidate(address, source, candidates.Count));
        }
    }

    private static IReadOnlyList<IPAddress> CreateCloudflareCandidateAddresses()
    {
        var addresses = new List<IPAddress>(CloudflareIPv4Ranges.Length + CloudflareIPv6Ranges.Length);

        for (var i = 0; i < CloudflareIPv4Ranges.Length; i++)
            addresses.Add(CloudflareIPv4Ranges[i].GetCandidateAddress());

        for (var i = 0; i < CloudflareIPv6Ranges.Length; i++)
            addresses.Add(CloudflareIPv6Ranges[i].GetCandidateAddress());

        return addresses;
    }

    private static IReadOnlyList<ConnectionCandidate> FilterCandidates(IReadOnlyList<ConnectionCandidate> candidates, AddressFamily addressFamily)
    {
        if (addressFamily != AddressFamily.InterNetwork && addressFamily != AddressFamily.InterNetworkV6)
            return candidates;

        var filteredCandidates = new List<ConnectionCandidate>(candidates.Count);

        foreach (var candidate in candidates)
        {
            if (candidate.Address.AddressFamily == addressFamily)
                filteredCandidates.Add(candidate);
        }

        return filteredCandidates;
    }

    private static async Task<CacheEntry> ResolveCandidatesAsync(string hostname)
    {
        var isCloudflareTarget = hostname.Equals(CLOUDFLARE_TARGET_HOST, StringComparison.OrdinalIgnoreCase);
        var directAddresses    = await ResolveDirectAddressesAsync(hostname, isCloudflareTarget).ConfigureAwait(false);

        if (directAddresses.Count == 0 && !isCloudflareTarget)
            throw new SocketException((int)SocketError.HostNotFound);

        var capacity      = directAddresses.Count + (isCloudflareTarget ? CloudflareCandidateAddresses.Count : 0);
        var candidates    = new List<ConnectionCandidate>(capacity);
        var seenAddresses = new HashSet<IPAddress>();

        AppendCandidates(candidates, seenAddresses, directAddresses, ConnectionCandidateSource.DirectDns);

        if (isCloudflareTarget)
            AppendCandidates(candidates, seenAddresses, CloudflareCandidateAddresses, ConnectionCandidateSource.CloudflareRange);

        if (candidates.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        Log.Verbose
        (
            "DNS 解析 {Host}: DirectDns={DirectCount}, CloudflareRange={CloudflareCount}, Total={TotalCount}",
            hostname,
            directAddresses.Count,
            isCloudflareTarget ? CloudflareCandidateAddresses.Count : 0,
            candidates.Count
        );

        return new CacheEntry(candidates, DateTimeOffset.UtcNow.AddSeconds(HOST_CACHE_SECONDS));
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveDirectAddressesAsync(string hostname, bool allowCloudflareFallback)
    {
        try
        {
            return await ResolveAddressesAsync(hostname).ConfigureAwait(false);
        }
        catch (Exception ex) when (allowCloudflareFallback)
        {
            Log.Warning(ex, "解析 {Host} 直连地址失败, 尝试 Cloudflare 网段候选", hostname);
            return [];
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAddressesAsync(string hostname)
    {
        if (IPAddress.TryParse(hostname, out var parsedAddress))
            return parsedAddress.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 ? [parsedAddress] : [];

        if (hostname.Equals("localhost", StringComparison.OrdinalIgnoreCase) || hostname.Equals("localhost.", StringComparison.OrdinalIgnoreCase))
            return [IPAddress.Loopback, IPAddress.IPv6Loopback];

        using var cts               = new CancellationTokenSource(TimeSpan.FromSeconds(DNS_RESOLVE_TIMEOUT_SECONDS));
        var       resolvedAddresses = await Dns.GetHostAddressesAsync(hostname, cts.Token).ConfigureAwait(false);

        if (resolvedAddresses.Length == 0)
            return [];

        var uniqueAddresses  = new HashSet<IPAddress>();
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

    private static IReadOnlyList<ConnectionCandidate> PrepareCandidates(string hostname, IReadOnlyList<ConnectionCandidate> cachedCandidates, AddressFamily addressFamily)
    {
        var filteredCandidates = FilterCandidates(cachedCandidates, addressFamily);
        var orderedCandidates  = ConnectionTelemetryStore.SortCandidates(hostname, filteredCandidates);
        var selectedCandidates = orderedCandidates;

        if (orderedCandidates.Count > MAX_CONNECTION_CANDIDATE_COUNT)
        {
            var selected = new List<ConnectionCandidate>(MAX_CONNECTION_CANDIDATE_COUNT);

            for (var i = 0; i < MAX_CONNECTION_CANDIDATE_COUNT; i++)
                selected.Add(orderedCandidates[i]);

            for (var i = 0; i < orderedCandidates.Count; i++)
            {
                var candidate = orderedCandidates[i];

                if (candidate.Source != ConnectionCandidateSource.DirectDns || selected.Contains(candidate))
                    continue;

                for (var replaceIndex = selected.Count - 1; replaceIndex >= 0; replaceIndex--)
                {
                    if (selected[replaceIndex].Source == ConnectionCandidateSource.DirectDns)
                        continue;

                    selected[replaceIndex] = candidate;
                    break;
                }
            }

            selectedCandidates = selected;
        }

        if (Log.IsEnabled(LogEventLevel.Verbose))
        {
            var candidateText = new StringBuilder(selectedCandidates.Count * 32);

            for (var i = 0; i < selectedCandidates.Count; i++)
            {
                if (i > 0)
                    candidateText.Append(", ");

                var candidate = selectedCandidates[i];
                candidateText.Append(candidate.Address);
                candidateText.Append('[');
                candidateText.Append(candidate.Source);
                candidateText.Append(']');
            }

            var signature = candidateText.ToString();
            var logKey    = string.Concat(hostname, "|", (int)addressFamily);

            if (!LastLoggedCandidateSignaturesByHost.TryGetValue(logKey, out var lastSignature) || lastSignature != signature)
            {
                LastLoggedCandidateSignaturesByHost[logKey] = signature;

                Log.Verbose
                (
                    "DNS 候选排序 {Host}: Total={TotalCount}, Selected={SelectedCount}, Top={TopCandidates}",
                    hostname,
                    filteredCandidates.Count,
                    selectedCandidates.Count,
                    signature
                );
            }
        }

        return selectedCandidates;
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

    #region Constants

    private const int DNS_RESOLVE_TIMEOUT_SECONDS = 4;

    private const string CLOUDFLARE_TARGET_HOST = "gh.atmoomen.top";

    private const int HOST_CACHE_SECONDS = 75;

    private const int MAX_CONNECTION_CANDIDATE_COUNT = 10;

    #endregion
}
