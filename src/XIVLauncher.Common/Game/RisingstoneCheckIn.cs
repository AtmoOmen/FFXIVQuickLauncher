#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Http;

namespace XIVLauncher.Common.Game;

public class RisingstoneCheckIn : IDisposable
{
    public Func<Task<string>>? RefreshRisingstoneCookieFunc { get; set; }

    private const string CheckInURL     = "https://apiff14risingstones.web.sdo.com/api/home/sign/signIn";
    private const string DailyRewardURL = "https://apiff14risingstones.web.sdo.com/api/home/sign/getSignReward";

    private static readonly HashSet<int> SuccessCode = 
    [
        10000, // 签到成功
        10001, // 今日已签到
    ];
    
    private static readonly HashSet<int> FailureCode = 
    [
        10301, // 操作太快
    ];

    private HttpClient? httpClient;

    private string savedCookie = string.Empty;

    // 签到
    [HttpRpc]
    public async Task<CheckInResult> ExecuteCheckIn()
    {
        try
        {
            if (this.httpClient == null) 
                this.InitHttpClient();

            if (string.IsNullOrWhiteSpace(this.savedCookie))
            {
                var cookie = await this.GetCookiesAsync();
                if (string.IsNullOrWhiteSpace(cookie))
                    return new CheckInResult { Success = false, Message = "无法获取 Cookie" };
            }

            await this.InitRisingStoneSessionAsync();

            // 执行签到
            var (success, code, message) = await this.RequestCheckInAsync();

            var result = new CheckInResult { Success = success, Code = code, Message = message };
            
            if (success)
            {
                // 领取签到奖励
                var currentMonth = DateTime.Now.ToString("yyyy-MM");
                var rewardList   = await this.RequestDailyRewardListAsync(currentMonth);

                if (rewardList.Count > 0)
                {
                    foreach (var reward in rewardList.Where(r => r.IsGet == 0)) // 0 表示可领取
                    {
                        var claimRewardResult = await this.RequestClaimDailyRewardAsync(reward.ID, currentMonth);
                        result.Message += $"\n领取奖励 {reward.ItemName}: {claimRewardResult}";
                        
                        await Task.Delay(1000);
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[石之家签到] 签到失败");
            return new CheckInResult { Success = false, Code = 0, Message = $"{ex.Message}" };
        }
    }

    public void Dispose()
    {
        this.httpClient?.Dispose();
        this.httpClient = null;
    }

    #region 签到

    private async Task<(bool Success, int Code, string Message)> RequestCheckInAsync()
    {
        if (this.httpClient == null)
            return (false, 0, "HttpClient 未初始化");

        var tempSuid = Guid.NewGuid().ToString();

        var content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("tempsuid", tempSuid)
        ]);

        using var req = new HttpRequestMessage(HttpMethod.Post, CheckInURL);
        req.Content = content;
        req.Headers.TryAddWithoutValidation("Origin",           "https://ff14risingstones.web.sdo.com");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("Referer",          "https://ff14risingstones.web.sdo.com/");
        req.Content?.Headers.ContentType?.CharSet = "UTF-8";

        var responseText = string.Empty;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var response = await this.httpClient.SendAsync(req);
            responseText = await response.Content.ReadAsStringAsync();
            if (!responseText.Contains("WAF", StringComparison.OrdinalIgnoreCase))
                break;

            var delayMs = 1500 + Random.Shared.Next(0, 1500) + (attempt * 1000);
            
            Log.Warning("[石之家签到] 被 WAF 规则拦截, 开始第 {0} 次重试, 等待 {1} 毫秒", attempt + 1, delayMs);
            
            await Task.Delay(delayMs);
        }

        try
        {
            var jsonDoc = JsonDocument.Parse(responseText);
            var code    = 0;
            var msg     = string.Empty;

            if (jsonDoc.RootElement.TryGetProperty("code", out var codeElement))
                code = codeElement.GetInt32();

            if (jsonDoc.RootElement.TryGetProperty("msg", out var msgElement))
                msg = msgElement.GetString() ?? string.Empty;

            return (SuccessCode.Contains(code), code, msg);
        }
        catch
        {
            return (false, 0, "解析响应失败");
        }
    }

    private async Task<List<DailyRewardItem>> RequestDailyRewardListAsync(string month)
    {
        if (this.httpClient == null)
            return [];

        var tempSuid = Guid.NewGuid().ToString();

        var response     = await this.httpClient.GetAsync($"https://apiff14risingstones.web.sdo.com/api/home/sign/signRewardList?month={month}&tempsuid={tempSuid}");
        var responseText = await response.Content.ReadAsStringAsync();

        var result = JsonSerializer.Deserialize<DailyRewardListResponse>(responseText);
        return result?.Data ?? [];
    }

    private async Task<string> RequestClaimDailyRewardAsync(int id, string month)
    {
        if (this.httpClient == null)
            return "错误: HttpClient 未初始化";

        var tempSuid = Guid.NewGuid().ToString();

        var content = new FormUrlEncodedContent
        ([
            new KeyValuePair<string, string>("id",       id.ToString()),
            new KeyValuePair<string, string>("month",    month),
            new KeyValuePair<string, string>("tempsuid", tempSuid)
        ]);

        using var req = new HttpRequestMessage(HttpMethod.Post, DailyRewardURL);
        req.Content = content;
        req.Headers.TryAddWithoutValidation("Origin",           "https://ff14risingstones.web.sdo.com");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("Referer",          "https://ff14risingstones.web.sdo.com/");
        req.Content?.Headers.ContentType?.CharSet = "UTF-8";

        var responseText = string.Empty;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var response = await this.httpClient.SendAsync(req);
            responseText = await response.Content.ReadAsStringAsync();
            if (!responseText.Contains("Request blocked by WAF", StringComparison.OrdinalIgnoreCase))
                break;

            var delayMs = 1500 + new Random().Next(0, 1500) + (attempt * 1000);
            await Task.Delay(delayMs);
        }

        try
        {
            var jsonDoc = JsonDocument.Parse(responseText);
            if (jsonDoc.RootElement.TryGetProperty("msg", out var msgElement))
                return msgElement.GetString() ?? "成功";
        }
        catch (JsonException)
        {
            // ignored
        }

        return responseText;
    }

    #endregion

    #region 工具

    private void InitHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies             = false
        };

        this.httpClient                       = new HttpClient(handler);
        this.httpClient.DefaultRequestVersion = new Version(2, 0);
        this.httpClient.DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionOrHigher;
        this.UpdateHttpClientHeaders();
    }

    private void UpdateHttpClientHeaders()
    {
        if (this.httpClient == null) return;

        this.httpClient.DefaultRequestHeaders.Clear();
        this.httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        this.httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8,en-GB;q=0.7,en-US;q=0.6");
        this.httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");
        this.httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Pragma", "no-cache");
        this.httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Ch-Ua", "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        this.httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", "?0");
        this.httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Ch-Ua-Platform", "\"Windows\"");
        this.httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        this.httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        this.httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        this.httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://ff14risingstones.web.sdo.com/");
        this.httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        if (!string.IsNullOrWhiteSpace(this.savedCookie)) 
            this.httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", this.savedCookie);
    }

    private async Task InitRisingStoneSessionAsync()
    {
        if (this.httpClient == null) return;

        try
        {
            var initPaths = new[]
            {
                "/api/home/GHome/isLogin",
                "/api/home/groupAndRole/getCharacterBindInfo?platform=2",
                "/api/home/sign/signRewardList?month=" + DateTime.Now.ToString("yyyy-MM")
            };

            foreach (var path in initPaths)
            {
                try
                {
                    var tempsuid = Guid.NewGuid().ToString();
                    var sep      = path.Contains('?') ? "&" : "?";
                    var url      = $"https://apiff14risingstones.web.sdo.com{path}{sep}tempsuid={tempsuid}";

                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("Origin",           "https://ff14risingstones.web.sdo.com");
                    req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                    req.Headers.TryAddWithoutValidation("Referer",          "https://ff14risingstones.web.sdo.com/");

                    using var resp = await this.httpClient.SendAsync(req);
                    await Task.Delay(100);
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private async Task<string> GetCookiesAsync()
    {
        if (this.RefreshRisingstoneCookieFunc == null)
            throw new Exception("RefreshRisingstoneCookieFunc is not set");

        var cookie = await this.RefreshRisingstoneCookieFunc();

        if (!string.IsNullOrWhiteSpace(cookie))
        {
            this.savedCookie = cookie;
            this.UpdateHttpClientHeaders();
        }

        return cookie;
    }

    #endregion

    #region Models

    public class CheckInResult
    {
        public bool      Success         { get; set; }
        public int       Code            { get; set; }
        public string    Message         { get; set; } = string.Empty;
        public DateTime? LastCheckInTime { get; set; }
    }

    public class DailyRewardListResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("msg")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public List<DailyRewardItem> Data { get; set; } = [];
    }

    public class DailyRewardItem
    {
        [JsonPropertyName("id")]
        public int ID { get; set; }

        [JsonPropertyName("begin_date")]
        public string BeginDate { get; set; } = string.Empty;

        [JsonPropertyName("end_date")]
        public string EndDate { get; set; } = string.Empty;

        [JsonPropertyName("rule")]
        public int Rule { get; set; }

        [JsonPropertyName("item_name")]
        public string ItemName { get; set; } = string.Empty;

        [JsonPropertyName("item_pic")]
        public string ItemPic { get; set; } = string.Empty;

        [JsonPropertyName("num")]
        public int Num { get; set; }

        [JsonPropertyName("item_desc")]
        public string ItemDesc { get; set; } = string.Empty;

        [JsonPropertyName("is_get")]
        public int IsGet { get; set; }
    }

    #endregion
}
