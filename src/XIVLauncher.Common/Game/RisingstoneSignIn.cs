using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace XIVLauncher.Common.Game
{
    /// <summary>
    /// 石之家签到服务 (仅提供 API，不包含调度逻辑)
    /// </summary>
    public class RisingstoneSignIn : IDisposable
    {
        private HttpClient? httpClient;
        private string savedCookie = string.Empty;
        
        /// <summary>
        /// 获取石之家 Cookie 的委托
        /// </summary>
        public Func<Task<string>>? RefreshCookieFunc { get; set; }
        
        public RisingstoneSignIn()
        {
        }
        
        /// <summary>
        /// 初始化 HttpClient
        /// </summary>
        private void InitializeHttpClient()
        {
            httpClient = new HttpClient();
            UpdateHttpClientHeaders();
        }
        
        /// <summary>
        /// 更新 HttpClient 的请求头
        /// </summary>
        private void UpdateHttpClientHeaders()
        {
            if (httpClient == null) return;
            
            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8,en-GB;q=0.7,en-US;q=0.6");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Pragma", "no-cache");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Ch-Ua", "\"Chromium\";v=\"122\", \"Not(A:Brand\";v=\"24\", \"Microsoft Edge\";v=\"122\"");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", "?0");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Ch-Ua-Platform", "\"Windows\"");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://ff14risingstones.web.sdo.com/");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 Edg/122.0.0.0");
            
            if (!string.IsNullOrWhiteSpace(savedCookie))
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", savedCookie);
        }
        
        /// <summary>
        /// 获取石之家登录用的 Cookie
        /// </summary>
        public async Task<string> GetCookie()
        {
            if (RefreshCookieFunc == null)
                throw new Exception("RefreshCookieFunc is not set");
            
            var cookie = await RefreshCookieFunc();
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                savedCookie = cookie;
                UpdateHttpClientHeaders();
            }
            return cookie;
        }
        
        /// <summary>
        /// 初始化石之家会话
        /// </summary>
        private async Task InitializeSessionAsync()
        {
            if (httpClient == null) return;

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
                        var sep = path.Contains('?') ? "&" : "?";
                        var url = $"https://apiff14risingstones.web.sdo.com{path}{sep}tempsuid={tempsuid}";

                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.TryAddWithoutValidation("Origin", "https://ff14risingstones.web.sdo.com");
                        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

                        using var resp = await httpClient.SendAsync(req);
                        await Task.Delay(100);
                    }
                    catch
                    {
                        // 忽略初始化请求的错误
                    }
                }
            }
            catch
            {
                // 忽略初始化错误
            }
        }
        
        /// <summary>
        /// 执行签到
        /// </summary>
        public async Task<SignInResult> ExecuteSignIn()
        {
            try
            {
                if (httpClient == null)
                {
                    InitializeHttpClient();
                }

                if (string.IsNullOrWhiteSpace(savedCookie))
                {
                    var cookie = await GetCookie();
                    if (string.IsNullOrWhiteSpace(cookie))
                        return new SignInResult { Success = false, Message = "无法获取 Cookie" };
                }

                // 初始化会话
                await InitializeSessionAsync();

                // 执行签到
                var (success, message) = await RequestSignInAsync();
                
                if (!success)
                    return new SignInResult { Success = false, Message = message };

                var results = new List<string> { $"签到: {message}" };

                // 自动领取奖励
                var currentMonth = DateTime.Now.ToString("yyyy-MM");
                var rewardList = await RequestSignRewardListAsync(currentMonth);

                if (rewardList.Count > 0)
                {
                    foreach (var reward in rewardList.Where(r => r.IsGet == 0)) // 0 表示可领取
                    {
                        var result = await RequestClaimSignRewardAsync(reward.ID, currentMonth);
                        results.Add($"领取 {reward.ItemName}: {result}");
                        await Task.Delay(1000);
                    }
                }
                
                return new SignInResult 
                { 
                    Success = true, 
                    Message = string.Join("\n", results),
                    LastSignInTime = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Risingstone] Sign in failed");
                return new SignInResult { Success = false, Message = $"签到异常: {ex.Message}" };
            }
        }
        
        /// <summary>
        /// 请求签到 API
        /// </summary>
        private async Task<(bool Success, string Message)> RequestSignInAsync()
        {
            if (httpClient == null) 
                return (false, "HttpClient 未初始化");

            var tempSuid = Guid.NewGuid().ToString();
            var url = $"https://apiff14risingstones.web.sdo.com/api/home/sign/signIn?tempsuid={tempSuid}";

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("tempsuid", tempSuid)
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            req.Headers.TryAddWithoutValidation("Origin", "https://ff14risingstones.web.sdo.com");
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

            var response = await httpClient.SendAsync(req);
            var responseText = await response.Content.ReadAsStringAsync();

            try
            {
                var jsonDoc = JsonDocument.Parse(responseText);
                var code = 0;
                var msg = string.Empty;
                
                if (jsonDoc.RootElement.TryGetProperty("code", out var codeElement))
                    code = codeElement.GetInt32();
                
                if (jsonDoc.RootElement.TryGetProperty("msg", out var msgElement))
                    msg = msgElement.GetString() ?? string.Empty;
                
                var formattedMessage = $"({code}){msg}";
                
                // 10000: 签到成功, 10001: 今日已签到, 10301: 操作太快
                if (code == 10000 || code == 10001 || code == 10301)
                    return (true, formattedMessage);
                
                return (false, formattedMessage);
            }
            catch
            {
                return (false, "解析响应失败");
            }
        }
        
        /// <summary>
        /// 请求签到奖励列表
        /// </summary>
        private async Task<List<SignRewardItem>> RequestSignRewardListAsync(string month)
        {
            if (httpClient == null) 
                return new List<SignRewardItem>();

            var tempSuid = Guid.NewGuid().ToString();
            var url = $"https://apiff14risingstones.web.sdo.com/api/home/sign/signRewardList?month={month}&tempsuid={tempSuid}";

            var response = await httpClient.GetAsync(url);
            var responseText = await response.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize<SignRewardListResponse>(responseText);
            return result?.Data ?? new List<SignRewardItem>();
        }
        
        /// <summary>
        /// 请求领取签到奖励
        /// </summary>
        private async Task<string> RequestClaimSignRewardAsync(int id, string month)
        {
            if (httpClient == null) 
                return "错误: HttpClient 未初始化";

            var tempSuid = Guid.NewGuid().ToString();
            var url = $"https://apiff14risingstones.web.sdo.com/api/home/sign/getSignReward?tempsuid={tempSuid}";

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("id", id.ToString()),
                new KeyValuePair<string, string>("month", month),
                new KeyValuePair<string, string>("tempsuid", tempSuid)
            });

            var response = await httpClient.PostAsync(url, content);
            var responseText = await response.Content.ReadAsStringAsync();

            try
            {
                var jsonDoc = JsonDocument.Parse(responseText);
                if (jsonDoc.RootElement.TryGetProperty("msg", out var msgElement))
                    return msgElement.GetString() ?? "成功";
            }
            catch (JsonException) { }

            return responseText;
        }
        
        public void Dispose()
        {
            httpClient?.Dispose();
            httpClient = null;
        }
        
        #region Models
        
        public class SignInResult
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public DateTime? LastSignInTime { get; set; }
        }

        public class SignRewardListResponse
        {
            [JsonPropertyName("code")]
            public int Code { get; set; }

            [JsonPropertyName("msg")]
            public string Message { get; set; } = string.Empty;

            [JsonPropertyName("data")]
            public List<SignRewardItem> Data { get; set; } = new();
        }

        public class SignRewardItem
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
}
