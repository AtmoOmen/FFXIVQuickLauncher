using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Serilog;
using XIVLauncher.Common.Game.Exceptions;

namespace XIVLauncher.Common.Game.DCTravel;

public partial class DCTravelClient
{
    public readonly  CancellationTokenSource KeepAliveCts;
    public           Func<Task<string>>      RefreshGameSessionByGuidFunc;
    public           Func<Task<string>>      RefreshDcTravelSessionIdFunc;
    public           Func<Task<string>>      RefreshGameSessionIdByAutoLoginFunc;
    public           Action<string>?         SetSdoAreaFunc = null;
    private readonly HttpClient              httpClient;
    private readonly CookieContainer         cookieContainer;
    private const    string                  BaseUrl = "ff14bjz.sdo.com";
    private const    string                  Domain  = "sdo.com";
    private          string                  ticket  = string.Empty;
    private          bool                    isInitialized;

    public DCTravelClient(string nSessionId)
    {
        //this.RefreshDcTravelSessionIdFunc = refreshDcTravelSessionIdFunc;
        //this.RefreshGameSessionByGuidFunc = refreshGameSessionIdFunc;
        KeepAliveCts    = new CancellationTokenSource();
        cookieContainer = new CookieContainer();
        if (!string.IsNullOrEmpty(nSessionId))
            cookieContainer.Add(new Cookie("nsessionid", nSessionId, "/", Domain));
        cookieContainer.Add(new Cookie("CAS_LOGIN_STATE",        "1", "/", Domain));
        cookieContainer.Add(new Cookie("SECURE_CAS_LOGIN_STATE", "1", "/", Domain));
        cookieContainer.Add(new Cookie("isLogin",                "1", "/", Domain));

        var handler = new HttpClientHandler
        {
            CookieContainer        = cookieContainer,
            UseCookies             = true,
            AllowAutoRedirect      = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        httpClient = new HttpClient(handler);
        var headers = new Dictionary<string, string>
        {
            { "Accept", "application/json" },
            { "Accept-Encoding", "gzip, deflate, br, zstd" },
            { "Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8,en-GB;q=0.7,en-US;q=0.6" },
            { "Content-Type", "application/json" },
            { "Priority", "u=1, i" },
            { "Sec-Ch-Ua", "\"Microsoft Edge\";v=\"137\", \"Chromium\";v=\"137\", \"Not/A)Brand\";v=\"24\"" },
            { "Sec-Ch-Ua-Mobile", "?0" },
            { "Sec-Ch-Ua-Platform", "\"Windows\"" },
            { "Sec-Fetch-Dest", "empty" },
            { "Sec-Fetch-Mode", "cors" },
            { "Sec-Fetch-Site", "same-origin" },
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36 Edg/137.0.0.0" }
        };
        foreach (var header in headers)
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
    }

    #region 初始化 认证

    public async Task GetValidCookie()
    {
        if (await InitTravelPage())
        {
            Log.Information("[DcTravel] Successfully initialized travel page.");
            isInitialized = true;
        }
        else
        {
            Log.Error("[DcTravel] Failed to initialize travel page. Need valid ticket");
            ticket = await RefreshDcTravelSessionIdFunc!.Invoke();
            await ValidateTicket();

            if (await InitTravelPage())
            {
                Log.Information("[DcTravel] Successfully initialized travel page.");
                isInitialized = true;
            }
        }
    }

    public async Task KeepCookieAlive()
    {
        while (!KeepAliveCts.Token.IsCancellationRequested)
        {
            if (!isInitialized)
            {
                await Task.Delay(1000, KeepAliveCts.Token);
                continue;
            }

            try
            {
                Log.Information("Cookie保活中...");
                await QueryGroupListTravelSource();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保活Cookie时出错");
            }

            var random      = new Random();
            var randomDelay = TimeSpan.FromMinutes(random.Next(30, 91));
            Log.Information($"下次Cookie保活将在 {randomDelay:mm\\:ss} 后进行");
            await Task.Delay(TimeSpan.FromMinutes(5), KeepAliveCts.Token); // 定期执行
        }
    }

    public string GetNSessionIdFromCookie()
    {
        var cookies    = cookieContainer.GetCookies(new Uri($"https://{BaseUrl}"));
        var nSessionId = cookies.First(x => x.Name == "nsessionid").Value;
        return nSessionId;
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
            Log.Error(ex, "Failed to initialize travel page");
            return false;
        }
    }

    public async Task ValidateTicket()
    {
        //https://ff14bjz.sdo.com/api/gmallinter/validateTicket?ticket=ULS21-000000000000000000000000
        _ = await GetRequestData("api/gmallinter/validateTicket", DCTravelAPIType.TravelWithTicket, new Dictionary<string, string> { { "ticket", ticket } }, true);
    }

    public async Task Logout()
    {
        if (isInitialized)
        {
            //https://ff14bjz.sdo.com/api/gmallinter/logout?
            _ = await GetRequestData("api/gmallinter/logout?", DCTravelAPIType.Order, new Dictionary<string, string>());
        }
    }

    #endregion

    #region 公共请求

    private void EnsureReturnCode(JsonNode node)
    {
        var returnCode = 0;
        if (node == null || (returnCode = node["return_code"].GetValue<int>()) != 0)
            throw new DCTravelAPIException($"API call failed with return code: {node?["return_code"]?.GetValue<int>()}, message: {node?["return_message"]?.GetValue<string>()}", returnCode);
    }

    private async Task<JsonNode> GetRequestData(string api, DCTravelAPIType type, Dictionary<string, string> parameters = null, bool ignoreInitialized = false)
    {
        if (!ignoreInitialized && !isInitialized)
            throw new DCTravelAPIException("DcTraveler is not initialized. Please call GetValidCookie() first.");
        var requestUri  = new Uri($"https://{BaseUrl}/{api}");
        var uriBuilder  = new UriBuilder(requestUri);
        var queryParams = HttpUtility.ParseQueryString(uriBuilder.Query);

        if (parameters != null && parameters.Count != 0)
        {
            foreach (var item in parameters)
                queryParams.Add(item.Key, item.Value);
        }

        uriBuilder.Query = queryParams.ToString();
        var tryNum = 3;

        while (tryNum-- > 0)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri))
                {
                    switch (type)
                    {
                        case DCTravelAPIType.Travel:
                            request.Headers.Add("Refer", "https://ff14bjz.sdo.com/RegionKanTelepo");
                            break;

                        case DCTravelAPIType.TravelWithTicket:
                            request.Headers.Add("Refer", $"https://ff14bjz.sdo.com/RegionKanTelepo?ticket={ticket}");
                            break;

                        case DCTravelAPIType.Order:
                            request.Headers.Add("Refer", "https://ff14bjz.sdo.com/orderList");
                            break;
                    }

                    Log.Debug($"[DcTravel] request: {uriBuilder.Uri}");
                    var response = await httpClient.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    var content = await response.Content.ReadAsStringAsync();
                    Log.Debug($"[DcTravel] response: {content}");
                    var node = JsonNode.Parse(content);
                    EnsureReturnCode(node);
                    return node["data"];
                }
            }
            catch (Exception ex)
            {
                if (ex is DCTravelAPIException dcEx)
                {
                    if (dcEx.IsNetworkTimeout)
                    {
                        await Task.Delay(5);
                        continue;
                    }

                    throw;
                }

                throw;
            }
        }

        throw new DCTravelAPIException("Failed to get request data after multiple attempts.");
    }

    #endregion

    #region 查询传送页面

    public class Character
    {
        [JsonPropertyName("roleId")] public string ContentId { get; set; }

        [JsonPropertyName("roleName")] public string Name { get; set; }

        public int AreaId  { get; set; }
        public int GroupId { get; set; }

        public string ToQueryString()
        {
            // Shit!
            return $"{{\"roleId\":\"{ContentId}\",\"roleName\":\"{Name}\",\"key\":0}}";
        }
    }

    //public enum MigrationStatus
    //{
    //    Failed = -1,
    //    InPrepare = 0,
    //    UnkownCompleted = 3,
    //    InQueue = 4,
    //    Completed = 5,
    //}
    public class OrderSatus
    {
        // 5 成功
        // 2 需要确认
        // 0,1 检查中
        // 3,4 处理中
        // -1 预检查失败
        // -5 传送失败
        public int    Status           { get; set; }
        public string CheckMessage     { get; set; }
        public string MigrationMessage { get; set; }
    }

    public async Task MigrationConfirmOrder(string orderId, bool confirmed) =>
        _ = await GetRequestData("api/gmallgateway/migrationConfirmOrder", DCTravelAPIType.Order, new Dictionary<string, string> { { "orderId", orderId }, { "confirmType", confirmed ? "1" : "0" } });

    #endregion

    #region 订单页面

    public enum TravelStatus
    {
        Failed,
        Arrival,
        Completed,
        Backing,
        Backed,
        Unknown
    }

    public class MigrationOrders
    {
        public int              TotalCount   { get; set; }
        public int              TotalPageNum { get; set; }
        public MigrationOrder[] Orders       { get; set; }
    }

    public class MigrationOrder
    {
        [JsonPropertyName("orderId")] public string OrderId { get; set; }

        [JsonPropertyName("roleId")] public string ContentId { get; set; }

        [JsonPropertyName("groupId")] public int GroupId { get; set; }

        [JsonPropertyName("groupCode")] public string GroupCode { get; set; }

        [JsonPropertyName("groupName")] public string GroupName { get; set; }

        [JsonPropertyName("createTime")] public string CreateTime { get; set; }
    }

    #endregion
}
