using System.Net;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;

namespace XIVLauncher.Common.Network;

public sealed class NetworkEnvironmentService : INetworkEnvironmentService
{
    public static INetworkEnvironmentService Shared { get; } = new NetworkEnvironmentService();

    private readonly HttpClient            httpClient;
    private readonly IReadOnlyList<string> traceURLs;

    private Task<NetworkEnvironmentInfo>? detectionTask;

    public NetworkEnvironmentService()
        : this
        (
            CreateHttpClient(),
            [Links.NETWORK_ENVIRONMENT_TRACE_URL, Links.CLOUDFLARE_TRACE_URL]
        )
    {
    }

    public NetworkEnvironmentService(HttpClient httpClient, IReadOnlyList<string> traceURLs)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(traceURLs);

        if (traceURLs.Count == 0)
            throw new ArgumentException("至少需要一个网络环境探测地址", nameof(traceURLs));

        this.httpClient = httpClient;
        this.traceURLs  = traceURLs.ToArray();
    }

    public Task<NetworkEnvironmentInfo> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var currentTask = Volatile.Read(ref detectionTask);
        if (currentTask == null)
        {
            var completion = new TaskCompletionSource<NetworkEnvironmentInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            currentTask = Interlocked.CompareExchange(ref detectionTask, completion.Task, null);

            if (currentTask == null)
            {
                currentTask = completion.Task;
                _ = DetectAndCompleteAsync(completion);
            }
        }

        return currentTask.WaitAsync(cancellationToken);
    }

    public Task<NetworkEnvironmentInfo> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<NetworkEnvironmentInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref detectionTask, completion.Task);
        _ = DetectAndCompleteAsync(completion);
        return completion.Task.WaitAsync(cancellationToken);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = XLHttpClientFactory.Create(TimeSpan.FromSeconds(3), 2, DecompressionMethods.All);
        client.Timeout = TimeSpan.FromSeconds(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");
        return client;
    }

    private async Task DetectAndCompleteAsync(TaskCompletionSource<NetworkEnvironmentInfo> completion)
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
        foreach (var traceURL in traceURLs)
        {
            try
            {
                using var request  = new HttpRequestMessage(HttpMethod.Get, traceURL);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var trace       = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var countryCode = ParseCountryCode(trace);
                if (countryCode == null)
                {
                    Log.Warning("网络环境探测响应缺少有效国家代码: {URL}", traceURL);
                    continue;
                }

                var region = string.Equals(countryCode, "CN", StringComparison.OrdinalIgnoreCase)
                                 ? NetworkRegion.MainlandChina
                                 : NetworkRegion.OutsideMainlandChina;
                var result = new NetworkEnvironmentInfo(region, countryCode, DateTimeOffset.UtcNow);
                Log.Information("网络环境检测完成: {Region}, 国家/地区代码: {CountryCode}", result.Region, result.CountryCode);
                return result;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "网络环境探测地址访问失败: {URL}", traceURL);
            }
        }

        Log.Warning("网络环境检测未获得有效结果");
        return CreateUnknownResult();
    }

    private static string? ParseCountryCode(string trace)
    {
        foreach (var line in trace.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith("loc=", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line[4..].Trim();
            if (value.Length != 2 || !char.IsAsciiLetter(value[0]) || !char.IsAsciiLetter(value[1]))
                return null;

            var countryCode = value.ToString().ToUpperInvariant();
            return countryCode == "XX" ? null : countryCode;
        }

        return null;
    }

    private static NetworkEnvironmentInfo CreateUnknownResult() =>
        new(NetworkRegion.Unknown, null, DateTimeOffset.UtcNow);
}
