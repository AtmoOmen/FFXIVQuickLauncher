using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game.Exceptions;

namespace XIVLauncher.Common.Game.DCTravel;

public partial class DCTravelClient
{
    public Func<Task<string>>?     RefreshGameSessionByGuidFunc        { get; set; }
    public Func<Task<string>>?     RefreshDcTravelSessionIDFunc        { get; set; }
    public Func<Task<string>>?     RefreshGameSessionIDByAutoLoginFunc { get; set; }
    public Action<string>?         SetSdoAreaFunc                      { get; set; }
    public CancellationTokenSource KeepAliveCancelSource               { get; private set; }

    private const string BASE_URL               = "ff14bjz.sdo.com";
    private const string DOMAIN                 = "sdo.com";
    private const int    KEEP_ALIVE_MIN_MINUTES = 30;
    private const int    KEEP_ALIVE_MAX_MINUTES = 91;

    private static readonly Uri BaseUri = new UriBuilder(Uri.UriSchemeHttps, BASE_URL).Uri;

    private static readonly FrozenDictionary<string, string> DefaultHeaders =
        new Dictionary<string, string>
        {
            ["Accept"]             = "application/json",
            ["Accept-Encoding"]    = "gzip, deflate, br, zstd",
            ["Accept-Language"]    = "zh-CN,zh;q=0.9,en;q=0.8,en-GB;q=0.7,en-US;q=0.6",
            ["Content-Type"]       = "application/json",
            ["Priority"]           = "u=1, i",
            ["Sec-Ch-Ua"]          = "\"Microsoft Edge\";v=\"137\", \"Chromium\";v=\"137\", \"Not/A)Brand\";v=\"24\"",
            ["Sec-Ch-Ua-Mobile"]   = "?0",
            ["Sec-Ch-Ua-Platform"] = "\"Windows\"",
            ["Sec-Fetch-Dest"]     = "empty",
            ["Sec-Fetch-Mode"]     = "cors",
            ["Sec-Fetch-Site"]     = "same-origin",
            ["User-Agent"]         = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36 Edg/137.0.0.0"
        }.ToFrozenDictionary();

    private bool IsInitialized =>
        Volatile.Read(ref initializedState) == 1;

    private readonly HttpClient      httpClient;
    private readonly CookieContainer cookieContainer;

    private string ticket = string.Empty;
    private int    initializedState;

    public DCTravelClient(string nSessionID)
    {
        KeepAliveCancelSource = new();

        cookieContainer = new();
        if (!string.IsNullOrEmpty(nSessionID))
            cookieContainer.Add(new Cookie("nsessionid", nSessionID, "/", DOMAIN));
        cookieContainer.Add(new Cookie("CAS_LOGIN_STATE",        "1", "/", DOMAIN));
        cookieContainer.Add(new Cookie("SECURE_CAS_LOGIN_STATE", "1", "/", DOMAIN));
        cookieContainer.Add(new Cookie("isLogin",                "1", "/", DOMAIN));

        var handler = new HttpClientHandler
        {
            CookieContainer        = cookieContainer,
            UseCookies             = true,
            AllowAutoRedirect      = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        foreach (var (key, value) in DefaultHeaders)
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
    }

    #region 查询超域旅行页面

    public async Task MigrationConfirmOrder(string orderId, bool confirmed) =>
        _ = await GetRequestData
            (
                "api/gmallgateway/migrationConfirmOrder",
                DCTravelAPIType.Order,
                new Dictionary<string, string>
                {
                    ["orderId"]     = string.IsNullOrWhiteSpace(orderId) ? throw new ArgumentException("orderId 不能为空或空白字符", nameof(orderId)) : orderId,
                    ["confirmType"] = confirmed ? "1" : "0"
                }
            );

    #endregion

    private static JsonObject EnsureReturnCode(JsonNode? node)
    {
        if (node is not JsonObject root)
            throw new DCTravelAPIException("API 响应不是有效的 JSON 对象");

        var returnCode = root["return_code"]?.GetValue<int>() ?? int.MinValue;

        if (returnCode != 0)
        {
            var message = root["return_message"]?.GetValue<string>() ?? "unknown";
            throw new DCTravelAPIException($"API 调用失败, 返回码: {returnCode}, 消息: {message}", returnCode);
        }

        return root;
    }

    private static Uri BuildRequestUri(string api, IReadOnlyDictionary<string, string>? parameters)
    {
        var requestUri = new Uri(BaseUri, api);
        if (parameters is not { Count: > 0 })
            return requestUri;

        var queryString = string.Join
        (
            "&",
            parameters
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value ?? string.Empty)}")
        );
        var uriBuilder = new UriBuilder(requestUri)
        {
            Query = queryString
        };
        return uriBuilder.Uri;
    }

    private void SetInitialized(bool value) =>
        Volatile.Write(ref initializedState, value ? 1 : 0);

    private async Task<JsonNode> GetRequestData
    (
        string                               api,
        DCTravelAPIType                      type,
        IReadOnlyDictionary<string, string>? parameters        = null,
        bool                                 ignoreInitialized = false
    )
    {
        if (!ignoreInitialized && !IsInitialized)
            throw new DCTravelAPIException("DcTraveler 未初始化, 请先调用 GetValidCookie()");

        const int MAX_ATTEMPTS = 3;

        var requestUri = BuildRequestUri(api, parameters);

        for (var attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
        {
            try
            {
                using var request = CreateRequest(requestUri, type);
                Log.Debug("[DCTravelClient] 请求: {RequestUri}", requestUri);
                using var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                var       content  = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Log.Debug("[DCTravelClient] 响应: {ResponseContent}", content);

                if (!response.IsSuccessStatusCode)
                {
                    throw new DCTravelAPIException
                    (
                        $"HTTP 请求失败, 状态码: {(int)response.StatusCode} ({response.StatusCode})",
                        (int)response.StatusCode
                    );
                }

                var root = EnsureReturnCode(JsonNode.Parse(content));
                return root["data"] ?? throw new DCTravelAPIException("API 响应缺少 'data' 字段");
            }
            catch (DCTravelAPIException ex) when (ex.IsNetworkTimeout && attempt < MAX_ATTEMPTS)
            {
                var delay = TimeSpan.FromMilliseconds(200 * attempt);
                Log.Warning(ex, "[DCTravelClient] 请求超时, 将在 {DelayMilliseconds}ms 后重试, 尝试次数: {Attempt}", delay.TotalMilliseconds, attempt);
                await Task.Delay(delay, KeepAliveCancelSource.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MAX_ATTEMPTS)
            {
                var delay = TimeSpan.FromMilliseconds(200 * attempt);
                Log.Warning(ex, "[DCTravelClient] HTTP 传输错误, 将在 {DelayMilliseconds}ms 后重试, 尝试次数: {Attempt}", delay.TotalMilliseconds, attempt);
                await Task.Delay(delay, KeepAliveCancelSource.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!KeepAliveCancelSource.Token.IsCancellationRequested && attempt < MAX_ATTEMPTS)
            {
                var delay = TimeSpan.FromMilliseconds(200 * attempt);
                Log.Warning(ex, "[DCTravelClient] HTTP 请求因超时取消, 将在 {DelayMilliseconds}ms 后重试, 尝试次数: {Attempt}", delay.TotalMilliseconds, attempt);
                await Task.Delay(delay, KeepAliveCancelSource.Token).ConfigureAwait(false);
            }
        }

        throw new DCTravelAPIException("多次尝试后获取请求数据失败");
    }

    private HttpRequestMessage CreateRequest(Uri requestUri, DCTravelAPIType type)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("Refer", ResolveReferer(type));
        return request;
    }

    private string ResolveReferer(DCTravelAPIType type) =>
        type switch
        {
            DCTravelAPIType.Travel           => Links.DC_TRAVEL_PAGE_URL,
            DCTravelAPIType.TravelWithTicket => $"{Links.DC_TRAVEL_PAGE_URL}?ticket={ticket}",
            DCTravelAPIType.Order            => new Uri(BaseUri, "orderList").ToString(),
            _                                => BaseUri.ToString()
        };

    #region 初始化 认证

    public async Task GetValidCookie()
    {
        if (await InitTravelPage())
        {
            Log.Information("[DCTravelClient] 成功初始化超域旅行页面");
            SetInitialized(true);
            return;
        }

        Log.Error("[DCTravelClient] 初始化超域旅行页面失败, 需要有效的 ticket");
        var refreshDcTravelSessionIdFunc = RefreshDcTravelSessionIDFunc ?? throw new DCTravelAPIException("未配置 RefreshDcTravelSessionIdFunc");
        ticket = await refreshDcTravelSessionIdFunc().ConfigureAwait(false);
        await ValidateTicket().ConfigureAwait(false);

        if (await InitTravelPage().ConfigureAwait(false))
        {
            Log.Information("[DCTravelClient] 成功初始化超域旅行页面");
            SetInitialized(true);
            return;
        }

        throw new DCTravelAPIException("验证 ticket 后初始化超域旅行页面失败");
    }

    public async Task KeepCookieAlive()
    {
        var cancellationToken = KeepAliveCancelSource.Token;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!IsInitialized)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            try
            {
                Log.Information("Cookie 保活中");
                await QueryGroupListTravelSource().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保活 Cookie 时出错");
            }

            var randomDelay = TimeSpan.FromMinutes(Random.Shared.Next(KEEP_ALIVE_MIN_MINUTES, KEEP_ALIVE_MAX_MINUTES));
            Log.Information("下次 Cookie 保活将在 {RandomDelay} 分钟后进行", randomDelay);
            await Task.Delay(randomDelay, cancellationToken);
        }
    }

    public string GetNSessionIdFromCookie()
    {
        var cookies = cookieContainer.GetCookies(BaseUri);
        var nSessionId = cookies
                         .FirstOrDefault(x => string.Equals(x.Name, "nsessionid", StringComparison.Ordinal))
                         ?.Value;

        return !string.IsNullOrWhiteSpace(nSessionId)
                   ? nSessionId
                   : throw new DCTravelAPIException("无 nsessionid Cookie 传递");
    }

    public async Task<bool> InitTravelPage()
    {
        //https://ff14bjz.sdo.com/api/orderserivce/pageInit?migrationType=4
        try
        {
            _ = await GetRequestData("api/orderserivce/pageInit", DCTravelAPIType.TravelWithTicket, new Dictionary<string, string> { { "migrationType", "4" } }, true);
            return true;
        }
        catch (DCTravelAPIException ex)
        {
            Log.Error(ex, "初始化超域旅行页面失败");
            return false;
        }
    }

    public async Task ValidateTicket()
    {
        if (string.IsNullOrWhiteSpace(ticket))
            throw new DCTravelAPIException("Ticket 为空");

        _ = await GetRequestData("api/gmallinter/validateTicket", DCTravelAPIType.TravelWithTicket, new Dictionary<string, string> { { "ticket", ticket } }, true);
    }

    public async Task Logout()
    {
        if (!IsInitialized)
            return;

        try
        {
            _ = await GetRequestData("api/gmallinter/logout", DCTravelAPIType.Order, ignoreInitialized: true);
            SetInitialized(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DCTravelClient] 登出失败");
        }
    }

    #endregion
}
