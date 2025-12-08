using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;

#if NET6_0_OR_GREATER && !WIN32
using System.Net.Security;
#endif

using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.Encryption;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.PlatformAbstractions;
using System.Threading;
using XIVLauncher.Common.Util;
using System.Text;
using Downloader;
using System.Security.Cryptography;

namespace XIVLauncher.Common.Game
{
    public class GuiLoginType
    {
        public LoginType LoginType { get; set; }
        public string DisplayName { get; set; }

        public static List<GuiLoginType> Get(bool showWeGameToken = false)
        {
            var types = new List<GuiLoginType>
            {
                new GuiLoginType { LoginType = LoginType.SdoSlide, DisplayName = "一键登录" },
                new GuiLoginType { LoginType = LoginType.SdoQrCode, DisplayName = "扫码登录" },
                new GuiLoginType { LoginType = LoginType.SdoStatic, DisplayName = "密码登录" },
                new GuiLoginType { LoginType = LoginType.WeGameSid, DisplayName = "WeGame SID"}
            };
            if (showWeGameToken)
            {
                types.Add(new GuiLoginType { LoginType = LoginType.WeGameToken, DisplayName = "WeGame抓包" });
            }
            return types;
        }
    }
    public enum LoginType
    {
        SdoStatic,
        SdoSlide,
        SdoQrCode,
        WeGameToken,
        WeGameSid,
        AutoLoginSession
    }

    public partial class Launcher
    {
        //private readonly string qrPath = Path.Combine(Environment.CurrentDirectory, "Resources", "QR.png");
        //private string AreaId = "1";
        //private const int autoLoginKeepTime = 30;

        //private static string userName = String.Empty;
        //private static string password = String.Empty;
        //private static LogEventHandler logEvent = null;
        //private static bool forceQR = false;
        //private static bool autoLogin = false;
        //private static string autoLoginSessionKey = null;

        private const int QRCodeExpirationTime = 300 * 1000;// ms
        private const int SlideExpirationTime = 30 * 1000;// ms
        private const int AutoLoginKeepDays = 30;
        //public DcTraveler DcTraveler;
        public Func<string, DcTraveler> CreateDcTraveler;
        public async Task<LoginResult> LoginBySid(string sndaId, string sid)
        {
            var oath = new OauthLoginResult
            {
                SndaId = sndaId,
                SessionId = sid,
                MaxExpansion = Constants.MaxExpansion,
                LoginType = LoginType.WeGameSid,
            };

            return new LoginResult
            {
                OauthLogin = oath,
                State = LoginState.Ok,
            };
        }

        public async Task<LoginResult> LoginBySdoStatic(string account, string password, DcTraveler dcTraveler, RisingstoneSignIn risingstoneSignIn)
        {
            var guid = await this.GetGuid();
            var macAddress = SdoUtils.GetMacAddress();
            //密码登录
            var result = await this.GetJsonAsSdoClient("staticLogin.json", new List<string>()
            {
                "checkCodeFlag=1", "encryptFlag=0", $"inputUserId={account}", $"password={password}", $"mac={macAddress}", $"guid={guid}",
                "inputUserType=0&accountDomain=1&autoLoginFlag=0&autoLoginKeepTime=0&supportPic=2"
            });

            if (result.ReturnCode != 0 || result.ErrorType != 0)
            {
                throw new SdoLoginException(result.ReturnCode, result.Data.FailReason);
            }

            if (string.IsNullOrEmpty(result.Data.Tgt))
            {
                throw new SdoLoginException((int)SdoLoginCustomExpectionCode.STATIC_NEED_CAPTCHA, $"本次登录需要输入验证码,目前暂不支持,请使用其他登录方式登录。");
            }

            Log.Information($"staticLogin.json:{result.Data.SndaId}:{result.Data.Tgt}");

            var sndaId = result.Data.SndaId;
            var tgt = result.Data.Tgt;
            if (dcTraveler != null)
            {
                dcTraveler.RefreshDcTravelSessionIdFunc = () => this.GetDcTravelSessionId(tgt, guid);
                dcTraveler.RefreshGameSessionByGuidFunc = () => this.GetSessionId(tgt, guid);
            }
            
            if (risingstoneSignIn != null)
            {
                risingstoneSignIn.RefreshRisingstoneCookieFunc = () => this.GetRisingstoneCookieAsync(tgt, guid);
            }

            var sessionId = await GetSessionId(tgt, guid);

            var oath = new OauthLoginResult
            {
                SessionId = sessionId,
                InputUserId = account,
                //Password = password,
                SndaId = sndaId,
                AutoLoginSessionKey = null,
                MaxExpansion = Constants.MaxExpansion,
                LoginType = LoginType.SdoStatic,
            };

            return new LoginResult
            {
                OauthLogin = oath,
                State = LoginState.Ok,
            };
        }

        public async Task<LoginResult> LoginByWeGameToken(string account, string token, bool autoLogin, DcTraveler dcTraveler, RisingstoneSignIn risingstoneSignIn)
        {
            var guid = await this.GetGuid();
            var (sndaId, tgt, autoLoginSessionKey) = await ThirdPartyLogin(account, token, autoLogin, AutoLoginKeepDays);
            if (dcTraveler != null)
            {
                dcTraveler.RefreshDcTravelSessionIdFunc = () => this.GetDcTravelSessionId(tgt, guid);
                dcTraveler.RefreshGameSessionByGuidFunc = () => this.GetSessionId(tgt, guid);
            }
            if (risingstoneSignIn != null)
            {
                risingstoneSignIn.RefreshRisingstoneCookieFunc = () => this.GetRisingstoneCookieAsync(tgt, guid);
            }
            
            var sessionId = await GetSessionId(tgt, guid);

            var oath = new OauthLoginResult
            {
                SessionId = sessionId,
                InputUserId = account,
                //Password = password,
                SndaId = sndaId,
                AutoLoginSessionKey = autoLogin ? autoLoginSessionKey : null,
                MaxExpansion = Constants.MaxExpansion,
                LoginType = LoginType.WeGameToken,
            };
            return new LoginResult
            {
                OauthLogin = oath,
                State = LoginState.Ok,
            };
        }

        public async Task<LoginResult> LoginByScanQrCode(bool autoLogin, CancellationTokenSource cts, Action<byte[]> showQrCode, DcTraveler dcTraveler, RisingstoneSignIn risingstoneSignIn)
        {
            var guid = await this.GetGuid();
            // Wait for Scan QrCode
            string? sndaId = null;
            string? tgt = null;
            string? account = null;

            while (!cts.IsCancellationRequested)
            {
                var (codeKey, qrCode, expiration) = await this.GetQRCode();
                showQrCode?.Invoke(qrCode);
                (sndaId, tgt, account) = await this.WaitingForScanQRCode(codeKey, guid, cts, expiration, AutoLoginKeepDays);
                if (sndaId != null)
                    break;
            }

            var newAccount = await this.GetAccountGroup(tgt, sndaId);
            account = string.IsNullOrEmpty(account) ? newAccount : account;
            string? autoLoginSessionKey = null;
            if (autoLogin)
                (tgt, autoLoginSessionKey) = await AccountGroupLogin(tgt, sndaId, AutoLoginKeepDays);

            if (dcTraveler != null)
            {
                dcTraveler.RefreshDcTravelSessionIdFunc = () => this.GetDcTravelSessionId(tgt, guid);
                dcTraveler.RefreshGameSessionByGuidFunc = () => this.GetSessionId(tgt, guid);
            }
            
            if (risingstoneSignIn != null)
            {
                risingstoneSignIn.RefreshRisingstoneCookieFunc = () => this.GetRisingstoneCookieAsync(tgt, guid);
            }
            
            var sessionId = await GetSessionId(tgt, guid);

            var oath = new OauthLoginResult
            {
                SessionId = sessionId,
                InputUserId = account,
                //Password = password,
                SndaId = sndaId,
                AutoLoginSessionKey = autoLogin ? autoLoginSessionKey : null,
                MaxExpansion = Constants.MaxExpansion,
                LoginType = LoginType.SdoQrCode
            };
            return new LoginResult
            {
                OauthLogin = oath,
                State = LoginState.Ok,
            };
        }

        public async Task<LoginResult> LoginBySlide(string account, bool autoLogin, CancellationTokenSource cts, Action<string> showVerificationCode, DcTraveler dcTraveler, RisingstoneSignIn risingstoneSignIn)
        {
            var guid = await this.GetGuid();
            // Wait for Slide
            await CancelPushMessageLogin(string.Empty, guid);
            var (pushMsgSerialNum, pushMsgSessionKey, expiration) = await SendPushMessage(account);
            showVerificationCode?.Invoke(pushMsgSerialNum);
            var (sndaId, tgt, autoLoginSessionKey) = await WaitingForSlideOnDaoyuApp(pushMsgSessionKey, pushMsgSerialNum, guid, expiration, cts, autoLogin, AutoLoginKeepDays);
            if (dcTraveler != null)
            {
                dcTraveler.RefreshDcTravelSessionIdFunc = () => this.GetDcTravelSessionId(tgt, guid);
                dcTraveler.RefreshGameSessionByGuidFunc = () => this.GetSessionId(tgt, guid);
            }
            
            if (risingstoneSignIn != null)
            {
                risingstoneSignIn.RefreshRisingstoneCookieFunc = () => this.GetRisingstoneCookieAsync(tgt, guid);
            }
            
            var sessionId = await GetSessionId(tgt, guid);

            var oath = new OauthLoginResult
            {
                SessionId = sessionId,
                InputUserId = account,
                //Password = password,
                SndaId = sndaId,
                AutoLoginSessionKey = autoLogin ? autoLoginSessionKey : null,
                MaxExpansion = Constants.MaxExpansion,
                LoginType = LoginType.SdoSlide
            };
            return new LoginResult
            {
                OauthLogin = oath,
                State = LoginState.Ok,
            };
        }

        public async Task<LoginResult> LoginBySessionKey(string account, string autoLoginSessionKey, DcTraveler dcTraveler, RisingstoneSignIn risingstoneSignIn)
        {
            var guid = await this.GetGuid();
            //快速登录,刷新SessionKey
            var (sndaId, tgt, newAutoLoginSessionKey) = await UpdateAutoLoginSessionKey(guid, autoLoginSessionKey);

            //快速登录
            var result = await this.GetJsonAsSdoClient("fastLogin.json", new List<string>() { $"tgt={tgt}", $"guid={guid}" });

            if (result.ReturnCode != 0)
            {
                throw new SdoLoginException(result.ReturnCode, result.Data.FailReason, true);
            }

            sndaId = result.Data.SndaId;
            tgt = result.Data.Tgt;

            try
            {
                if (dcTraveler != null)
                {
                    dcTraveler.RefreshDcTravelSessionIdFunc = () => this.GetDcTravelSessionId(tgt, guid);
                    dcTraveler.RefreshGameSessionByGuidFunc = () => this.GetSessionId(tgt, guid);
                }
                
                if (risingstoneSignIn != null)
                {
                    risingstoneSignIn.RefreshRisingstoneCookieFunc = () => this.GetRisingstoneCookieAsync(tgt, guid);
                }
                
                var sessionId = await GetSessionId(tgt, guid);
                var oath = new OauthLoginResult
                {
                    SessionId = sessionId,
                    InputUserId = account,
                    //Password = password,
                    SndaId = sndaId,
                    AutoLoginSessionKey = newAutoLoginSessionKey,
                    MaxExpansion = Constants.MaxExpansion,
                    LoginType = LoginType.AutoLoginSession
                };
                return new LoginResult
                {
                    OauthLogin = oath,
                    State = LoginState.Ok,
                };
            }
            catch (Exception ex)
            {
                if (ex is SdoLoginException sdoEx)
                {
                    sdoEx.RemoveAutoLoginSessionKey = true;
                    throw sdoEx;
                }
                else
                {
                    throw;
                }
            }

        }

        public async Task<string> GetSessionId(string tgt, string guid)
        {
            var promotionResult = await GetPromotionInfo(tgt);
            return await SsoLogin(tgt, guid);
        }

        public async Task<string> GetDcTravelSessionId(string tgt, string guid)
        {
            var promotionResult = await GetPromotionInfo(tgt, "https://ff14bjz.sdo.com/RegionKanTelepo");
            return await SsoLogin(tgt, guid);
        }

        /// <summary>
        /// 获取石之家登录的 ticket(appId=6788)
        /// </summary>
        public async Task<string> GetRisingstoneTicket(string tgt, string guid)
        {
            const string RISINGSTONE_APP_ID = "6788";
            // serviceUrl 需要 URL 编码
            var serviceUrl = "https://apiff14risingstones.web.sdo.com/api/home/GHome/login?redirectUrl=https://ff14risingstones.web.sdo.com/pc/index.html";
            Log.Information($"[Risingstone] GetRisingstoneTicket: tgt={tgt?.Substring(0, Math.Min(10, tgt?.Length ?? 0))}..., guid={guid}, appId={RISINGSTONE_APP_ID}");
            
            try
            {
                // appId=6788 调用 GetPromotionInfo
                var promotionResult = await GetPromotionInfoWithAppId(tgt, serviceUrl, RISINGSTONE_APP_ID);
                Log.Information($"[Risingstone] GetPromotionInfo success: returnCode={promotionResult.ReturnCode}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Risingstone] GetPromotionInfo failed");
                throw;
            }
            
            // appId=6788 调用 SsoLogin
            var ticket = await SsoLoginWithAppId(tgt, guid, RISINGSTONE_APP_ID);
            Log.Information($"[Risingstone] SsoLogin success: ticket={ticket?.Substring(0, Math.Min(20, ticket?.Length ?? 0))}...");
            return ticket;
        }

        /// <summary>
        /// 获取石之家登录 Cookie (ff14risingstones=xxx)
        /// </summary>
        public async Task<string> GetRisingstoneCookieAsync(string tgt, string guid)
        {
            var ticket = await GetRisingstoneTicket(tgt, guid);
            Log.Information($"[Risingstone] Got ticket: {ticket?.Substring(0, Math.Min(20, ticket?.Length ?? 0))}...");
            
            var cookieContainer = new CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                AllowAutoRedirect = true,  // 允许自动重定向
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };
            
            using var httpClient = new HttpClient(handler);
            
            // 设置更完整的浏览器请求头
            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
            // 不设置 Accept-Encoding，让 HttpClientHandler 自动处理
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8,en-GB;q=0.7,en-US;q=0.6");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Connection", "keep-alive");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Pragma", "no-cache");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Ch-Ua", "\"Chromium\";v=\"122\", \"Not(A:Brand\";v=\"24\", \"Microsoft Edge\";v=\"122\"");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", "?0");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Ch-Ua-Platform", "\"Windows\"");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "none");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-User", "?1");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 Edg/122.0.0.0");

            try
            {
                // 第一步：先访问石之家主页，获取初始 Cookie 和建立 WAF 会话
                Log.Information("[Risingstone] Step 1: Visiting homepage to establish session...");
                var homepageUrl = "https://ff14risingstones.web.sdo.com/pc/index.html";
                
                using var homeResp = await httpClient.GetAsync(homepageUrl);
                Log.Information($"[Risingstone] Homepage response status: {homeResp.StatusCode}");
                
                // 稍等一下，模拟用户行为
                await Task.Delay(500);
                
                // 第二步：调用登录 API（带上 Referer）
                var initialUrl = $"https://apiff14risingstones.web.sdo.com/api/home/GHome/login?redirectUrl=https://ff14risingstones.web.sdo.com/pc/index.html&ticket={ticket}";
                Log.Information($"[Risingstone] Step 2: Calling login API...");
                
                // 更新请求头，添加 Referer
                httpClient.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://ff14risingstones.web.sdo.com/");
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://ff14risingstones.web.sdo.com");
                
                using var loginResp = await httpClient.GetAsync(initialUrl);
                var loginRespText = await loginResp.Content.ReadAsStringAsync();
                Log.Information($"[Risingstone] Login API response status: {loginResp.StatusCode}");
                Log.Information($"[Risingstone] Login API response: {loginRespText}");
                
                // 检查是否被 WAF 拦截
                if ((int)loginResp.StatusCode == 567 || loginRespText.Contains("EdgeOne") || loginRespText.Contains("AccessDeny"))
                {
                    Log.Error("[Risingstone] Request blocked by WAF (Tencent Cloud EdgeOne)");
                    throw new Exception("Request blocked by WAF. Please try again later.");
                }
                
                // 检查登录 API 响应
                if (loginRespText.Contains("\"code\""))
                {
                    try
                    {
                        var loginJson = System.Text.Json.JsonDocument.Parse(loginRespText);
                        if (loginJson.RootElement.TryGetProperty("code", out var codeElement))
                        {
                            var code = codeElement.GetInt32();
                            if (code != 10000 && code != 0)
                            {
                                var msg = loginJson.RootElement.TryGetProperty("msg", out var msgElement) 
                                    ? msgElement.GetString() 
                                    : "Unknown error";
                                Log.Error($"[Risingstone] Login API returned error: code={code}, msg={msg}");
                                throw new Exception($"Risingstone login failed: ({code}){msg}");
                            }
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // 不是 JSON 响应，可能是重定向页面，继续处理
                    }
                }
                
                // 第三步：请求目标页面完成会话
                await Task.Delay(200);
                Log.Information("[Risingstone] Step 3: Finalizing session...");
                
                using var finalResp = await httpClient.GetAsync(homepageUrl);
                Log.Information($"[Risingstone] Final page response status: {finalResp.StatusCode}");

                // 第四步：调用 isLogin API 触发 Cookie 设置
                httpClient.DefaultRequestHeaders.Remove("Accept");
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                httpClient.DefaultRequestHeaders.Remove("Sec-Fetch-Dest");
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                httpClient.DefaultRequestHeaders.Remove("Sec-Fetch-Mode");
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

                var isLoginUrl = $"https://apiff14risingstones.web.sdo.com/api/home/GHome/isLogin?tempsuid={Guid.NewGuid()}";
                using var isLoginResp = await httpClient.GetAsync(isLoginUrl);
                var isLoginText = await isLoginResp.Content.ReadAsStringAsync();
                Log.Information($"[Risingstone] isLogin response: {isLoginText}");

                // 验证 isLogin 响应，确认登录成功
                try
                {
                    var isLoginJson = System.Text.Json.JsonDocument.Parse(isLoginText);
                    if (isLoginJson.RootElement.TryGetProperty("code", out var codeElement))
                    {
                        var code = codeElement.GetInt32();
                        if (code != 10000)
                        {
                            var msg = isLoginJson.RootElement.TryGetProperty("msg", out var msgElement) 
                                ? msgElement.GetString() 
                                : "Not logged in";
                            Log.Error($"[Risingstone] isLogin check failed: code={code}, msg={msg}");
                            throw new Exception($"Risingstone login verification failed: ({code}){msg}");
                        }
                        Log.Information("[Risingstone] isLogin check passed, user is logged in");
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    Log.Error(ex, "[Risingstone] Failed to parse isLogin response");
                    throw new Exception("Failed to verify Risingstone login status");
                }

                // 从 CookieContainer 中提取 ff14risingstones Cookie
                var allCookies = cookieContainer.GetAllCookies();
                Log.Information($"[Risingstone] Total cookies collected: {allCookies.Count}");
                
                foreach (Cookie cookie in allCookies)
                {
                    Log.Debug($"[Risingstone] Cookie: {cookie.Name}={cookie.Value?.Substring(0, Math.Min(20, cookie.Value?.Length ?? 0))}... Domain={cookie.Domain}");
                }
                
                Cookie risingstoneCookie = null;
                foreach (Cookie c in allCookies)
                {
                    if (c.Name.Equals("ff14risingstones", StringComparison.OrdinalIgnoreCase))
                    {
                        risingstoneCookie = c;
                        break;
                    }
                }

                if (risingstoneCookie != null && !string.IsNullOrWhiteSpace(risingstoneCookie.Value))
                {
                    Log.Information("[Risingstone] Successfully obtained cookie");
                    return $"ff14risingstones={risingstoneCookie.Value}";
                }

                Log.Error("[Risingstone] ff14risingstones cookie not found in response");
                throw new Exception("Failed to obtain ff14risingstones cookie");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Risingstone] Failed to get cookie");
                throw;
            }
        }

        public enum SdoLoginState
        {
            GotQRCode,
            WaitingScanQRCode,
            LoginSucess,
            LoginFail,
            WaitingConfirm,
            OutTime
        }

        private async Task<string> GetGuid()
        {
            var result = await this.GetJsonAsSdoClient("getGuid.json", new List<string>() { "generateDynamicKey=1" });

            if (result.ErrorType != 0)
                throw new OauthLoginException(result.ToString());

            var dynamicKey = result.Data.DynamicKey;
            return result.Data.Guid;
        }

        private async Task<(string sndaId, string tgt, string autoLoginSessionKey)> UpdateAutoLoginSessionKey(string guid, string autoLoginSessionKey)
        {
            var result = await this.GetJsonAsSdoClient("autoLogin.json", new List<string>() { $"autoLoginSessionKey={autoLoginSessionKey}", $"guid={guid}" });
            //-10515005 "对不起，自动登录已失效，请重新登录"
            if (result.ReturnCode != 0)
                throw new SdoLoginException(result.ReturnCode, result.Data.FailReason, true);
            Log.Information($"LoginSessionKey Updated, {(result.Data.AutoLoginMaxAge / 3600f):F1} hours left");
            autoLoginSessionKey = result.Data.AutoLoginSessionKey;
            var tgt = result.Data.Tgt;
            var sndaId = result.Data.SndaId;
            return (sndaId, tgt, autoLoginSessionKey);
        }

        #region 手机APP滑动登陆

        private async Task CancelPushMessageLogin(string pushMsgSessionKey, string guid)
        {
            // /authen/cancelPushMessageLogin.json
            await this.GetJsonAsSdoClient("cancelPushMessageLogin.json", new List<string>() { $"pushMsgSessionKey={pushMsgSessionKey}", $"guid={guid}" });
        }

        private async Task<(string pushMsgSerialNum, string pushMsgSessionKey, CancellationTokenSource slideExpiration)> SendPushMessage(string account)
        {
            var slideExpiration = new CancellationTokenSource();
            slideExpiration.CancelAfter(SlideExpirationTime);

            var result = await this.GetJsonAsSdoClient("sendPushMessage.json", new List<string>() { $"inputUserId={account}" });

            ///authen/sendPushMessage.json
            //-14001710 请确保已安装叨鱼，并保持联网
            //-1602726  该账号首次在本设备上登录，不支持一键登录，请使用二维码登录
            //-10516808 用户未确认
            if (result.ReturnCode != 0)
            {
                throw new SdoLoginException(result.ReturnCode, result.Data.FailReason);
            }
            var pushMsgSerialNum = result.Data.PushMsgSerialNum;
            var pushMsgSessionKey = result.Data.PushMsgSessionKey;
            return (pushMsgSerialNum, pushMsgSessionKey, slideExpiration);
        }

        private async Task<(string sndaId, string tgt, string AutoLoginSessionKey)> WaitingForSlideOnDaoyuApp(
            string pushMsgSessionKey,
            string pushMsgSerialNum,
            string guid,
            CancellationTokenSource slideExpiration,
            CancellationTokenSource userCancel,
            bool autoLogin,
            int autoLoginKeepDays
            )
        {
            while (!slideExpiration.IsCancellationRequested && !userCancel.IsCancellationRequested)
            {
                var result = await this.GetJsonAsSdoClient(
                    "pushMessageLogin.json",
                    autoLogin
                        ? new List<string>() { $"pushMsgSessionKey={pushMsgSessionKey}", $"guid={guid}", "autoLoginFlag=1", $"autoLoginKeepTime={autoLoginKeepDays}" }
                        : new List<string>() { $"pushMsgSessionKey={pushMsgSessionKey}", $"guid={guid}" }
                    );
                switch (result.ReturnCode)
                {
                    case 0:
                        return (result.Data.SndaId, result.Data.Tgt, result.Data.AutoLoginSessionKey);
                    case -10516808:
                        //-10516808 用户未确认
                        await Task.Delay(1000).ConfigureAwait(false);
                        continue;
                    default:
                        throw new SdoLoginException(result.ReturnCode, result.Data.FailReason);
                }
            }
            throw new SdoLoginException((int)SdoLoginCustomExpectionCode.SLIDE_TIMEOUT_OR_CANCELED, "登录超时或被取消");
        }

        #endregion

        #region 扫码登陆

        private async Task<(string codeKey, byte[] qrCode, CancellationTokenSource cts)> GetQRCode()
        {
            // /authen/getCodeKey.json
            var qrCodeExpiration = new CancellationTokenSource();
            qrCodeExpiration.CancelAfter(QRCodeExpirationTime);

            var response = await this.SendSdoHttpRequestAsync(HttpMethod.Get, "getCodeKey.json", new List<string>() { $"maxsize=89" });
            var cookies = response.Headers.SingleOrDefault(header => header.Key == "Set-Cookie").Value;
            var codeKey = cookies.FirstOrDefault(x => x.StartsWith("CODEKEY="))?.Split(';')[0];
            codeKey = codeKey?.Split('=')[1];
            if (string.IsNullOrEmpty(codeKey))
            {
                throw new OauthLoginException("QRCode下载失败");
            }
            var bytes = await response.Content.ReadAsByteArrayAsync();

            Log.Information($"QRCode下载完成,CodeKey={codeKey}");
            return (codeKey, bytes, qrCodeExpiration);
        }

        private async Task<(string sndaId, string tgt, string account)> WaitingForScanQRCode(string codeKey, string guid, CancellationTokenSource qrCodeExpiration, CancellationTokenSource userCancel, int autoLoginKeepDays)
        {
            while (!qrCodeExpiration.IsCancellationRequested && !userCancel.IsCancellationRequested)
            {
                var result = await this.GetJsonAsSdoClient("codeKeyLogin.json",
                                                           new List<string>() { $"codeKey={codeKey}", $"guid={guid}", $"autoLoginFlag=1", $"autoLoginKeepTime={autoLoginKeepDays}", $"maxsize=97" });

                if (result.ReturnCode == 0 && result.Data.NextAction == 0)
                {
                    return (result.Data.SndaId, result.Data.Tgt, result.Data.InputUserId);
                }

                if (result.ReturnCode == -10515805)
                {
                    //logEvent?.Invoke(SdoLoginState.WaitingScanQRCode, "等待用户扫码...");
                    await Task.Delay(1000).ConfigureAwait(false);
                    continue;
                }
                throw new OauthLoginException(result.Data.FailReason);
            }
            throw new SdoLoginException((int)SdoLoginCustomExpectionCode.SLIDE_TIMEOUT_OR_CANCELED, "登录超时或被取消");
        }

        #endregion

        #region 登陆结束

        private async Task<string> SsoLogin(string tgt, string guid)
        {
            // /authen/ssoLogin.json 抓包的ticket=SID
            var result = await this.GetJsonAsSdoClient("ssoLogin.json", new List<string>() { $"tgt={tgt}", $"guid={guid}" }, tgt);

            if (result.ReturnCode != 0)
                throw new SdoLoginException(result.ReturnCode, result.Data.FailReason);
            return result.Data.Ticket;
        }

        private async Task<SdoLoginResult> GetPromotionInfo(string tgt, string serviceUrl = null)
        {
            // /authen/getPromotion.json 激活ticket的登录权限
            var paras = new List<string>() { $"tgt={tgt}" };
            if (serviceUrl != null)
            {
                paras.Add($"serviceUrl={serviceUrl}");
            }
            var result = await this.GetJsonAsSdoClient("getPromotionInfo.json", paras, tgt);
            if (result.ReturnCode != 0)
                throw new SdoLoginException(result.ReturnCode, result.Data.FailReason);
            return result;
        }

        /// <summary>
        /// 使用指定 appId 调用 SsoLogin
        /// </summary>
        private async Task<string> SsoLoginWithAppId(string tgt, string guid, string appId)
        {
            var result = await this.GetJsonAsSdoClient("ssoLogin.json", new List<string>() { $"tgt={tgt}", $"guid={guid}" }, tgt, appId);

            if (result.ReturnCode != 0)
                throw new SdoLoginException(result.ReturnCode, result.Data.FailReason);
            return result.Data.Ticket;
        }

        /// <summary>
        /// 使用指定 appId 调用 GetPromotionInfo
        /// </summary>
        private async Task<SdoLoginResult> GetPromotionInfoWithAppId(string tgt, string serviceUrl, string appId)
        {
            var paras = new List<string>() { $"tgt={tgt}" };
            if (serviceUrl != null)
            {
                paras.Add($"serviceUrl={serviceUrl}");
            }
            var result = await this.GetJsonAsSdoClient("getPromotionInfo.json", paras, tgt, appId);
            if (result.ReturnCode != 0)
                throw new SdoLoginException(result.ReturnCode, result.Data.FailReason);
            return result;
        }

        #endregion

        #region 第三方登录

        private async Task<(string sndaId, string tgt, string key)> ThirdPartyLogin(string thridUserId, string token, bool autoLogin, int autoLoginKeepDays)
        {
            //Log.Error($"TOKEN:{token}");
            //第三方登录
            var result = await this.GetJsonAsSdoClient("thirdPartyLogin",
                                                       new List<string>()
                                                       {
                                                           "companyid=310", "islimited=0", $"thridUserId={thridUserId}", $"token={token}",
                                                           autoLogin ? $"autoLoginFlag=1&autoLoginKeepTime={autoLoginKeepDays}" : "autoLoginFlag=0&autoLoginKeepTime=0"
                                                       });

            if (result.ReturnCode != 0)
            {
                throw new SdoLoginException(result.ReturnCode, result.Data.FailReason);
            }

            Log.Information($"thirdPartyLogin:{result.Data.SndaId}:{result.Data.Tgt}");

            return (result.Data.SndaId, result.Data.Tgt, result.Data.AutoLoginSessionKey);

        }

        #endregion

        #region AccountGroup

        private async Task<string> GetAccountGroup(string tgt, string sndaId)
        {
            var result = await this.GetJsonAsSdoClient("getAccountGroup", new List<string>() { "serviceUrl=http%3A%2F%2Fwww.sdo.com", $"tgt={tgt}" });

            if (result.ReturnCode != 0 || result.ErrorType != 0)
            {
                throw new SdoLoginException(result.ReturnCode, result.Data.FailReason);
            }

            if (!result.Data.SndaIdArray.Contains(sndaId))
                throw new SdoLoginException((int)SdoLoginCustomExpectionCode.SCAN_QRCODE_GET_ACCOUNT_FAIL, $"获取用户名失败");

            Log.Information($"getAccountGroup:{string.Join(",", result.Data.SndaIdArray)}");

            return result.Data.SndaIdArray.Contains(sndaId) ? result.Data.AccountArray[result.Data.SndaIdArray.IndexOf(sndaId)] : null;
        }

        #endregion

        #region AccountGroupLogin

        private async Task<(string tgt, string autoLoginSessionKey)> AccountGroupLogin(string tgt, string sndaId, int autoLoginKeepDays)
        {
            var result = await this.GetJsonAsSdoClient("accountGroupLogin",
                                                       new List<string>() { "serviceUrl=http%3A%2F%2Fwww.sdo.com", $"tgt={tgt}", $"sndaId={sndaId}", "autoLoginFlag=1", $"autoLoginKeepTime={autoLoginKeepDays}" });
            Log.Information($"accountGroupLogin:AutoLoginMaxAge:{result.Data.AutoLoginMaxAge}");

            if (result.ReturnCode != 0)
                throw new SdoLoginException(result.ReturnCode, result.Data.FailReason);
            return (result.Data.Tgt, result.Data.AutoLoginSessionKey);
        }

        #endregion

        public class SdoLoginResult
        {
            [JsonProperty("error_type")]
            public int ErrorType;

            [JsonProperty("return_code")]
            public int ReturnCode;

            [JsonProperty("data")]
            public SdoLoginData Data;

            public class SdoLoginData
            {
                [JsonProperty("failReason")]
                public string FailReason;

                [JsonProperty("nextAction")]
                public int NextAction;

                [JsonProperty("guid")]
                public string Guid;

                [JsonProperty("pushMsgSerialNum")]
                public string PushMsgSerialNum;

                [JsonProperty("pushMsgSessionKey")]
                public string PushMsgSessionKey;

                [JsonProperty("dynamicKey")]
                public string DynamicKey;

                [JsonConverter(typeof(MaskMiddleConverter))]
                [JsonProperty("ticket")]
                public string Ticket;

                [JsonConverter(typeof(MaskMiddleConverter))]
                [JsonProperty("sndaId")]
                public string SndaId;

                [JsonConverter(typeof(MaskMiddleConverter))]
                [JsonProperty("tgt")]
                public string Tgt;

                [JsonConverter(typeof(MaskMiddleConverter))]
                [JsonProperty("autoLoginSessionKey")]
                public string AutoLoginSessionKey;

                [JsonProperty("autoLoginMaxAge")]
                public int AutoLoginMaxAge;

                [JsonConverter(typeof(MaskMiddleConverter))]
                [JsonProperty("inputUserId")]
                public string InputUserId;

                [JsonConverter(typeof(MaskMiddleConverter))]
                [JsonProperty("accountArray")]
                public List<string> AccountArray;

                [JsonConverter(typeof(MaskMiddleConverter))]
                [JsonProperty("sndaIdArray")]
                public List<string> SndaIdArray;
            }

            public string ToLog()
            {
                var settings = new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                };
                return JsonConvert.SerializeObject(this, Formatting.Indented, settings);
            }
        }

        private bool useGlobalDomain = true;

        private Task<HttpResponseMessage> SendSdoHttpRequestAsync(HttpMethod method, string endPoint, List<string> para, string tgt = null, string appId = "100001900")
        {
            var request = this.GetSdoHttpRequestMessage(method, endPoint, para, tgt, appId);
            var response = this.loginClient.SendAsync(request);
            return response;
        }

        private HttpRequestMessage GetSdoHttpRequestMessage(HttpMethod method, string endPoint, List<string> para, string tgt = null, string appId = "100001900")
        {
            var mac = SdoUtils.GetMac();
            var commonParas = new List<string>();
            commonParas.Add("authenSource=1");
            commonParas.Add($"appId={appId}");
            commonParas.Add($"areaId=1");
            commonParas.Add($"appIdSite={appId}");
            commonParas.Add("locale=zh_CN");
            commonParas.Add("productId=4");
            commonParas.Add("frameType=1");
            commonParas.Add("endpointOS=1");
            commonParas.Add("version=21");
            commonParas.Add("customSecurityLevel=2");
            commonParas.Add($"deviceId={SdoUtils.GetDeviceId()}");
            commonParas.Add($"thirdLoginExtern=0");
            commonParas.Add($"macId={mac}");
            commonParas.Add($"productVersion=1%2e9%2e7%2e10");
            commonParas.Add($"tag=0");
            para.AddRange(commonParas);
            var casDomain = useGlobalDomain ? "cas.sdo.com" : "n1.cas.sdo.com";
            var request = new HttpRequestMessage(method, $"https://{casDomain}/authen/{endPoint}?{string.Join("&", para)}");
            //Log.Information($"https://cas.sdo.com/authen/{endPoint}?{string.Join("&", para)}");
            request.Headers.AddWithoutValidation("Cache-Control", "no-cache");
            request.Headers.AddWithoutValidation("User-Agent", "Mozilla/4.0 (compatible; MSIE 8.0; Windows NT 5.1; Trident/4.0; Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.1; SV1) ; InfoPath.2; .NET CLR 2.0.50727; MS-RTC LM 8; .NET CLR 3.0.04506.648; .NET CLR 3.5.21022; .NET CLR 1.1.4322; .NET CLR 3.0.4506.2152; .NET CLR 3.5.30729)");
            request.Headers.AddWithoutValidation("Host", "cas.sdo.com");
            if (endPoint is "ssoLogin.json" or "getPromotionInfo.json")
            {
                request.Headers.AddWithoutValidation("Cookie", $"CASTGC={tgt}; CAS_LOGIN_STATE=1");
                //Log.Information($"Added Cookie:CASCID={CASCID}; SECURE_CASCID={SECURE_CASCID}; CASTGC=***; CAS_LOGIN_STATE=1");
            }
            var cookies = this.loginCookies.GetAllCookies();
            var hasCid = cookies.Cast<Cookie>().Any(cookie => string.Equals(cookie.Name, "CASCID", StringComparison.OrdinalIgnoreCase));
            if (!hasCid)
            {
                var randomCid = $"CID{SdoUtils.GetMD5(ASCIIEncoding.ASCII.GetBytes(mac))}";
                Log.Information($"Use MD5 of MAC address as CASCID");
                request.Headers.AddWithoutValidation("Cookie", $"CASCID={randomCid}; SECURE_CASCID={randomCid};");
            }
            return request;
        }


        private async Task<SdoLoginResult> GetJsonAsSdoClient(string endPoint, List<string> para, string tgt = null, string appId = "100001900")
        {
            try {
                var response = await this.SendSdoHttpRequestAsync(HttpMethod.Get, endPoint, para, tgt, appId);
                var reply = await response.Content.ReadAsStringAsync();
                try
                {
                    var result = JsonConvert.DeserializeObject<SdoLoginResult>(reply);
                    Log.Information($"{endPoint}:ErrorType={result.ErrorType}:ReturnCode={result.ReturnCode}:FailReason:{result.Data.FailReason}:NextAction={result.Data.NextAction}");
                    Log.Debug($"GetJsonAsSdoClient({endPoint}):\n{result.ToLog()}");
                    return result;
                }
                catch (JsonReaderException ex)
                {
                    throw new JsonReaderException($"{ex.Message}\n {reply}");
                }
            }
            catch (Exception ex)
            {
                if (this.useGlobalDomain)
                {
                    Log.Error(ex, "GetJsonAsSdoClient");
                    Log.Error("Switch to n1.cas.sdo.com");
                    this.useGlobalDomain = false;
                    return await this.GetJsonAsSdoClient(endPoint, para, tgt, appId);
                }
                else
                {
                    throw;
                }
            }
        }

        public Process? LaunchGameSdo(IGameRunner runner, string sessionId, string sndaId, int dcTravelPort, int risingstonePort, string areaId, string lobbyHost, string gmHost, string dbHost, string areasInfo,
                                      string additionalArguments, DirectoryInfo gamePath, bool encryptArguments, DpiAwareness dpiAwareness)
        {
            Log.Information(
                $"XivGame::LaunchGame(args:{additionalArguments})");
            //EnsureLoginEntry(gamePath);
            var exePath = Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe");

            var environment = new Dictionary<string, string>();
            var argumentBuilder = new ArgumentBuilder()
                                  .Append("-AppID", "100001900")
                                  .Append("-AreaID", areaId)
                                  .Append("Dev.LobbyHost01", lobbyHost)
                                  .Append("Dev.LobbyPort01", "54994")
                                  .Append("Dev.GMServerHost", gmHost)
                                  .Append("Dev.SaveDataBankHost", dbHost)
                                  .Append("resetConfig", "0")
                                  .Append("DEV.MaxEntitledExpansionID", "1")
                                  .Append("DEV.TestSID", sessionId)
                                  .Append("XL.SndaId", sndaId)
                                  .Append("XL.LobbyHosts", $"{areasInfo}")
                                  .Append("XL.DcTraveler", $"{dcTravelPort}")
                                  .Append("XL.Risingstone", $"{risingstonePort}");
            // This is a bit of a hack; ideally additionalArguments would be a dictionary or some KeyValue structure
            if (!string.IsNullOrEmpty(additionalArguments))
            {
                var regex = new Regex(@"\s*(?<key>[^=]+)\s*=\s*(?<value>[^\s]+)\s*", RegexOptions.Compiled);
                foreach (Match match in regex.Matches(additionalArguments))
                    argumentBuilder.Append(match.Groups["key"].Value, match.Groups["value"].Value);
            }

            if (!File.Exists(exePath))
                throw new BinaryNotPresentException(exePath);

            var workingDir = Path.Combine(gamePath.FullName, "game");

            var arguments = encryptArguments
                                ? argumentBuilder.BuildEncrypted()
                                : argumentBuilder.Build();

            return runner.Start(exePath, workingDir, arguments, environment, dpiAwareness);
        }

        public void EnsureLoginEntry(DirectoryInfo gamePath)
        {
            var bootPath = Path.Combine(gamePath.FullName, "sdo", "sdologin");
            var entryDll = Path.Combine(bootPath, "sdologinentry64.dll");
            var xlEntryDll = Path.Combine(Paths.ResourcesPath, "sdologinentry64.dll");

            if (!File.Exists(xlEntryDll))
            {
                xlEntryDll = Path.Combine(Paths.ResourcesPath, "binaries", "sdologinentry64.dll");
            }

            try
            {
                if (!Directory.Exists(bootPath))
                {
                    // 没有sdo文件夹的纯净客户端
                    Directory.CreateDirectory(bootPath);
                }

                if (!File.Exists(entryDll))
                {
                    Log.Information($"未发现sdologinentry64.dll,将复制${xlEntryDll}");
                    File.Copy(xlEntryDll, entryDll, true);
                }
                else
                {
                    if (FileVersionInfo.GetVersionInfo(entryDll).CompanyName == "ottercorp")
                    {
                        if (GetFileHash(entryDll) != GetFileHash(xlEntryDll))
                        {
                            Log.Information($"xlEntryDll:{entryDll}版本不一致,替换sdologinentry64.dll");
                            File.Copy(xlEntryDll, entryDll, true);
                        }
                        else
                        {
                            Log.Information($"sdologinentry64.dll校验成功");
                            return;
                        }
                    }
                    else
                    {
                        // 备份盛趣的sdologinentry64.dll 为 sdologinentry64.sdo.dll
                        Log.Information($"检测到sdologinentry64.dll不是ottercorp版本,备份原文件并替换");
                        File.Copy(entryDll, Path.Combine(bootPath, "sdologinentry64.sdo.dll"), true);
                        File.Copy(xlEntryDll, entryDll, true);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"未能复制{xlEntryDll}至{entryDll}\n请检查程序是否有{entryDll}的写入权限,或者{gamePath.FullName}目录下的游戏正在运行。\n{ex.Message}");
            }
        }

        public async Task<LoginResult> CheckGameUpdate(SdoArea area, DirectoryInfo gamePath, bool forceBaseVersion)
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"http://{area.AreaPatch}/http/win32/shanda_release_chs_game/{(forceBaseVersion ? Constants.BASE_GAME_VERSION : Repository.Ffxiv.GetVer(gamePath))}");

            request.Headers.AddWithoutValidation("X-Hash-Check", "enabled");
            request.Headers.AddWithoutValidation("User-Agent", Constants.PatcherUserAgent);

            EnsureVersionSanity(gamePath, Constants.MaxExpansion);
            request.Content = new StringContent(GetVersionReport(gamePath, Constants.MaxExpansion, forceBaseVersion));

            var resp = await this.client.SendAsync(request);
            var text = await resp.Content.ReadAsStringAsync();

            // Conflict indicates that boot needs to update, we do not get a patch list or a unique ID to download patches with in this case
            if (resp.StatusCode == HttpStatusCode.Conflict)
                return new LoginResult { PendingPatches = null, State = LoginState.NeedsPatchBoot, OauthLogin = null };

            if (!resp.Headers.TryGetValues("X-Patch-Unique-Id", out var uidVals))
            {
                Log.Error($"RequestUri:{request.RequestUri}");
                Log.Error($"Content:{request.Content}");
                Log.Error($"Response:{text}");
                throw new InvalidResponseException("Could not get X-Patch-Unique-Id.", text);
            }

            var uid = uidVals.First();

            if (string.IsNullOrEmpty(text))
                return new LoginResult { PendingPatches = null, State = LoginState.Ok, OauthLogin = null };

            Log.Verbose("Game Patching is needed... List:\n{PatchList}", text);

            var pendingPatches = PatchListParser.Parse(text);
            return new LoginResult { PendingPatches = pendingPatches, State = LoginState.NeedsPatchGame, OauthLogin = new OauthLoginResult() };
        }
    }
}
