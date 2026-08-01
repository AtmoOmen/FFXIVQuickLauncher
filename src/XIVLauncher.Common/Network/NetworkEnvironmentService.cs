using System.Net;
using System.Text.Json;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;

namespace XIVLauncher.Common.Network;

public sealed class NetworkEnvironmentService : INetworkEnvironmentService
{
    public static INetworkEnvironmentService Shared { get; } = new NetworkEnvironmentService();

    private readonly HttpClient            httpClient;
    private readonly IReadOnlyList<string> detectionURLs;
    private readonly TimeProvider          timeProvider;

    private Task<NetworkEnvironmentInfo>? detectionTask;

    public NetworkEnvironmentService()
        : this
        (
            CreateHttpClient(),
            [Links.IPIP_LOCATION_URL, Links.CLOUDFLARE_TRACE_URL],
            TimeProvider.System
        )
    {
    }

    public NetworkEnvironmentService
    (
        HttpClient            httpClient,
        IReadOnlyList<string> detectionURLs,
        TimeProvider          timeProvider
    )
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(detectionURLs);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (detectionURLs.Count == 0)
            throw new ArgumentException("至少需要一个网络环境探测地址", nameof(detectionURLs));

        this.httpClient    = httpClient;
        this.detectionURLs = [.. detectionURLs];
        this.timeProvider  = timeProvider;
    }

    public Task<NetworkEnvironmentInfo> GetCurrentAsync
    (
        CancellationToken cancellationToken = default
    )
    {
        while (true)
        {
            var currentTask = Volatile.Read(ref detectionTask);
            if (currentTask != null && IsDetectionCurrent(currentTask))
                return currentTask.WaitAsync(cancellationToken);

            var completion = new TaskCompletionSource<NetworkEnvironmentInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref detectionTask, completion.Task, currentTask) != currentTask)
                continue;

            _ = DetectAndCompleteAsync(completion);
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    public Task<NetworkEnvironmentInfo> RefreshAsync
    (
        CancellationToken cancellationToken = default
    )
    {
        var completion = new TaskCompletionSource<NetworkEnvironmentInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref detectionTask, completion.Task);
        _ = DetectAndCompleteAsync(completion);
        return completion.Task.WaitAsync(cancellationToken);
    }

    private bool IsDetectionCurrent
    (
        Task<NetworkEnvironmentInfo> task
    )
    {
        if (!task.IsCompleted)
            return true;

        if (!task.IsCompletedSuccessfully)
            return false;

        var result = task.Result;
        var cacheDuration = result.Region == NetworkRegion.Unknown ?
                                TimeSpan.FromSeconds(30) :
                                TimeSpan.FromMinutes(5);
        return timeProvider.GetUtcNow() - result.DetectedAtUTC < cacheDuration;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = XLHttpClientFactory.Create(TimeSpan.FromSeconds(3), 2, DecompressionMethods.All);
        client.Timeout = TimeSpan.FromSeconds(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");
        return client;
    }

    private async Task DetectAndCompleteAsync
    (
        TaskCompletionSource<NetworkEnvironmentInfo> completion
    )
    {
        try
        {
            completion.TrySetResult(await DetectAsync().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "检测网络环境时发生未预期错误");
            completion.TrySetResult(CreateUnknownResult());
        }
    }

    private async Task<NetworkEnvironmentInfo> DetectAsync()
    {
        foreach (var detectionURL in detectionURLs)
        {
            try
            {
                using var request  = new HttpRequestMessage(HttpMethod.Get, detectionURL);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content   = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var detection = ParseDetectionResult(content);

                if (detection == null)
                {
                    Log.Warning("网络环境探测响应缺少有效地域信息: {URL}", detectionURL);
                    continue;
                }

                var (region, countryCode) = detection.Value;
                var result = new NetworkEnvironmentInfo(region, countryCode, timeProvider.GetUtcNow());
                Log.Information("网络环境检测完成: {Region}, 国家/地区代码: {CountryCode}", result.Region, result.CountryCode);
                return result;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "网络环境探测地址访问失败: {URL}", detectionURL);
            }
        }

        Log.Warning("网络环境检测未获得有效结果");
        return CreateUnknownResult();
    }

    private static (NetworkRegion Region, string? CountryCode)? ParseDetectionResult
    (
        string content
    )
    {
        var countryCode = ParseCountryCode(content);

        if (countryCode != null)
        {
            var region = string.Equals(countryCode, "CN", StringComparison.OrdinalIgnoreCase) ?
                             NetworkRegion.ChineseMainland :
                             NetworkRegion.NotChineseMainland;
            return (region, countryCode);
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var       root     = document.RootElement;
            if (!root.TryGetProperty("ret", out var status)                                  ||
                !string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("data", out var data)                                   ||
                !data.TryGetProperty("location", out var location)                           ||
                location.ValueKind        != JsonValueKind.Array                             ||
                location.GetArrayLength() == 0)
                return null;

            var country = location[0].GetString();
            if (string.IsNullOrWhiteSpace(country))
                return null;

            return string.Equals(country, "中国", StringComparison.Ordinal) ?
                       (MainlandChina: NetworkRegion.ChineseMainland, "CN") :
                       (NetworkRegion.NotChineseMainland, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ParseCountryCode
    (
        string trace
    )
    {
        foreach (var line in trace.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith("loc=", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line[4..].Trim();
            if (value.Length != 2 || !char.IsAsciiLetter(value[0]) || !char.IsAsciiLetter(value[1]))
                return null;

            var countryCode = value.ToString().ToUpperInvariant();
            return countryCode == "XX" ?
                       null :
                       countryCode;
        }

        return null;
    }

    private NetworkEnvironmentInfo CreateUnknownResult() =>
        new(NetworkRegion.Unknown, null, timeProvider.GetUtcNow());
}
