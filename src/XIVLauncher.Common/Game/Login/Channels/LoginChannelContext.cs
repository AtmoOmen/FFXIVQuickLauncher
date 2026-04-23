using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game.DCTravel;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Game.Login.Channels;

public sealed class LoginChannelContext
{
    private readonly HttpClient            loginHttpClient;
    private readonly CookieContainer       loginCookies;
    private readonly DeviceProfileSnapshot deviceProfile;
    private readonly string                casCID;

    private int casDomainMode;

    public LoginChannelContext(DeviceProfileSnapshot deviceProfile)
    {
        ArgumentNullException.ThrowIfNull(deviceProfile);

        this.deviceProfile = deviceProfile;
        loginCookies       = new CookieContainer();
        var loginHandler = new HttpClientHandler
        {
            UseCookies      = true,
            CookieContainer = loginCookies
        };

        loginHttpClient = new HttpClient(loginHandler);
        casCID          = deviceProfile.CasCid;
        casDomainMode   = 0;
    }

    public static LoginResult BuildOkLoginResult
    (
        string    account,
        string    sid,
        string    sessionID,
        string?   autoLoginSessionKey,
        LoginType loginType
    )
    {
        var oath = new OAuthLoginResult
        {
            SessionID           = sessionID,
            InputUserID         = account,
            SndaID              = sid,
            AutoLoginSessionKey = autoLoginSessionKey,
            MaxExpansion        = Constants.MaxExpansion,
            LoginType           = loginType
        };

        return new LoginResult
        {
            OAuthLogin = oath,
            State      = LoginState.Ok
        };
    }

    public Task<LoginResponse> GetJsonAsync
    (
        string       endPoint,
        List<string> paras,
        string?      tgt   = null,
        string       appId = SdoInfos.APP_ID
    ) =>
        GetJsonAsSdoClient(endPoint, paras, tgt, appId);

    public async Task<string> GetGuidAsync()
    {
        var result = await GetJsonAsSdoClient("getGuid.json", ["generateDynamicKey=1"]).ConfigureAwait(false);

        if (result.ErrorType != 0)
            throw new OAuthLoginException(result.ToString());

        return result.Data.Guid;
    }

    public Task<LoginResponse> GetSafePhoneSystemConfigAsync() =>
        GetJsonAsSdoClient("/authen/v2/getSystemConfig?logintype=godown", []);

    public Task<LoginResponse> InitSafePhoneSmsLoginAsync(string account, string? flowId = null, bool isVoice = false)
    {
        var paras = new List<string>(3)
        {
            $"inputUserId={account}",
            $"isVoice={(isVoice ? 1 : 0)}"
        };

        if (!string.IsNullOrWhiteSpace(flowId))
            paras.Add($"flowId={flowId}");

        return GetJsonAsSdoClient("/authen/v2/safePhoneSmsLogin/init", paras);
    }

    public Task<LoginResponse> VerifySafePhoneCaptchaAsync(string flowId, string captchaInfo) =>
        GetJsonAsSdoClient("/authen/v2/safePhoneSmsLogin/verifyCaptcha", [$"captchaInfo={captchaInfo}", $"flowId={flowId}"]);

    public Task<LoginResponse> ConfirmSafePhoneSendAsync(string flowId, bool isVoice = false) =>
        GetJsonAsSdoClient("/authen/v2/safePhoneSmsLogin/confirmSend", [$"flowId={flowId}", $"isVoice={(isVoice ? 1 : 0)}"]);

    public Task<LoginResponse> ConfirmSafePhoneLoginAsync(string account, string flowId, string verifyCode, bool keepLogin) =>
        GetJsonAsSdoClient
        (
            "/authen/v2/safePhoneSmsLogin/confirmLogin",
            [$"flowId={flowId}", $"inputUserId={account}", $"verifyCode={verifyCode}", $"keepLoginFlag={(keepLogin ? 1 : 0)}"]
        );

    public Task<LoginResponse> CheckCodeLoginAsync(string guid, string captchaCode, bool keepLogin)
    {
        var captchaInfo = JsonConvert.SerializeObject
        (
            new
            {
                picCode = captchaCode
            }
        );

        return GetJsonAsSdoClient
        (
            "checkCodeLogin.json",
            [
                $"guid={guid}",
                $"password={Uri.EscapeDataString(captchaCode)}",
                "challenge=",
                "validate=",
                "seccode=",
                "outInfo=",
                $"captchaInfo={Uri.EscapeDataString(captchaInfo)}",
                $"keepLoginFlag={(keepLogin ? 1 : 0)}"
            ]
        );
    }

    public async Task<byte[]> DownloadCaptchaImageAsync(string captchaUrl, CancellationToken cancellationToken = default)
    {
        var       requestUri = BuildCaptchaUri(captchaUrl);
        using var request    = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.AddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.AddWithoutValidation
        (
            "User-Agent",
            "Mozilla/4.0 (compatible; MSIE 8.0; Windows NT 5.1; Trident/4.0; Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.1; SV1) ; InfoPath.2; .NET CLR 2.0.50727; MS-RTC LM 8; .NET CLR 3.0.04506.648; .NET CLR 3.5.21022; .NET CLR 1.1.4322; .NET CLR 3.0.4506.2152; .NET CLR 3.5.30729)"
        );
        request.Headers.AddWithoutValidation("Host", requestUri.Host);

        using var response = await loginHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(string SID, string TGT, string AutoLoginSessionKey)> UpdateAutoLoginSessionKeyAsync
    (
        string guid,
        string autoLoginSessionKey
    )
    {
        var result = await GetJsonAsSdoClient("autoLogin.json", [$"autoLoginSessionKey={autoLoginSessionKey}", $"guid={guid}"]).ConfigureAwait(false);
        if (result.ReturnCode != 0)
            throw new LoginException(result.ReturnCode, result.Data.FailReason, true);

        return (result.Data.SndaID, result.Data.Tgt, result.Data.AutoLoginSessionKey);
    }

    public async Task<(string SID, string TGT, string Key)> ThirdPartyLoginAsync
    (
        string thirdUserID,
        string token,
        bool   autoLogin,
        int    autoLoginKeepDays
    )
    {
        var result = await GetJsonAsSdoClient
                     (
                         "thirdPartyLogin",
                         [
                             "companyid=310", "islimited=0", $"thridUserId={thirdUserID}", $"token={token}",
                             autoLogin ? $"autoLoginFlag=1&autoLoginKeepTime={autoLoginKeepDays}" : "autoLoginFlag=0&autoLoginKeepTime=0"
                         ]
                     ).ConfigureAwait(false);

        if (result.ReturnCode != 0)
            throw new LoginException(result.ReturnCode, result.Data.FailReason);

        return (result.Data.SndaID, result.Data.Tgt, result.Data.AutoLoginSessionKey);
    }

    public async Task<string?> GetAccountGroupAsync(string tgt, string sid)
    {
        var result = await GetJsonAsSdoClient("getAccountGroup", [$"serviceUrl={Uri.EscapeDataString(Links.SDO_SERVICE_URL)}", $"tgt={tgt}"]).ConfigureAwait(false);

        if (result.ReturnCode != 0 || result.ErrorType != 0)
            throw new LoginException(result.ReturnCode, result.Data.FailReason);

        var index = result.Data.SndaIDArray.IndexOf(sid);
        if (index < 0)
            throw new LoginException((int)LoginExceptionCode.ScanQrCodeGetAccountFail, "扫描二维码后获取用户名失败");

        return result.Data.AccountArray[index];
    }

    public async Task<(string TGT, string AutoLoginSessionKey)> AccountGroupLoginAsync
    (
        string tgt,
        string sid,
        int    autoLoginKeepDays
    )
    {
        var result = await GetJsonAsSdoClient
                     (
                         "accountGroupLogin",
                         [$"serviceUrl={Uri.EscapeDataString(Links.SDO_SERVICE_URL)}", $"tgt={tgt}", $"sndaId={sid}", "autoLoginFlag=1", $"autoLoginKeepTime={autoLoginKeepDays}"]
                     ).ConfigureAwait(false);

        if (result.ReturnCode != 0)
            throw new LoginException(result.ReturnCode, result.Data.FailReason);

        return (result.Data.Tgt, result.Data.AutoLoginSessionKey);
    }

    public async Task CancelPushMessageLoginAsync(string pushMSGSessionKey, string guid) =>
        _ = await GetJsonAsSdoClient("cancelPushMessageLogin.json", [$"pushMsgSessionKey={pushMSGSessionKey}", $"guid={guid}"]).ConfigureAwait(false);

    public async Task<(string PushMSGSerialNum, string PushMSGSessionKey, CancellationTokenSource SlideExpiration)> SendPushMessageAsync(string account, int slideExpirationTime)
    {
        var slideExpiration = new CancellationTokenSource();
        slideExpiration.CancelAfter(slideExpirationTime);

        var result = await GetJsonAsSdoClient("sendPushMessage.json", [$"inputUserId={account}"]).ConfigureAwait(false);
        if (result.ReturnCode != 0)
            throw new LoginException(result.ReturnCode, result.Data.FailReason);

        return (result.Data.PushMsgSerialNum, result.Data.PushMsgSessionKey, slideExpiration);
    }

    public async Task<(string CodeKey, byte[] QRCode, CancellationTokenSource CTS)> GetQRCodeAsync(int qrCodeExpirationTime)
    {
        var qrCodeExpiration = new CancellationTokenSource();
        qrCodeExpiration.CancelAfter(qrCodeExpirationTime);

        var response = await SendSdoHttpRequestAsync(HttpMethod.Get, "getCodeKey.json", ["maxsize=89"]).ConfigureAwait(false);
        var cookies  = response.Headers.TryGetValues("Set-Cookie", out var setCookieValues) ? setCookieValues : [];
        var codeKey  = cookies.FirstOrDefault(x => x.StartsWith("CODEKEY=", StringComparison.Ordinal))?.Split(';')[0];
        codeKey = codeKey?.Split('=')[1];
        if (string.IsNullOrEmpty(codeKey))
            throw new OAuthLoginException("QRCode下载失败");

        var bytes = await response.Content.ReadAsByteArrayAsync(qrCodeExpiration.Token).ConfigureAwait(false);
        return (codeKey, bytes, qrCodeExpiration);
    }

    public async Task<string> GetSessionIdAsync(string tgt, string guid)
    {
        _ = await GetPromotionInfoAsync(tgt).ConfigureAwait(false);
        return await SsoLoginAsync(tgt, guid).ConfigureAwait(false);
    }

    public async Task<string> GetDCTravelSessionIDAsync(string tgt, string guid)
    {
        _ = await GetPromotionInfoAsync(tgt, Links.DC_TRAVEL_PAGE_URL).ConfigureAwait(false);
        return await SsoLoginAsync(tgt, guid).ConfigureAwait(false);
    }

    public void BindDCTravelSessionRefresh(DCTravelClient? dcTravelClient, string tgt, string guid)
    {
        if (dcTravelClient == null)
            return;

        dcTravelClient.RefreshDcTravelSessionIDFunc = () => GetDCTravelSessionIDAsync(tgt, guid);
        dcTravelClient.RefreshGameSessionByGuidFunc = () => GetSessionIdAsync(tgt, guid);
    }

    private static LoginResponse DeserializeLoginResponse(string endPoint, string reply)
    {
        try
        {
            var result = JsonConvert.DeserializeObject<LoginResponse>(reply)!;
            Log.Information
            (
                "{EndPoint}:ErrorType={ResultErrorType}:ReturnCode={ResultReturnCode}:FailReason:{DataFailReason}:NextAction={DataNextAction}",
                endPoint,
                result.ErrorType,
                result.ReturnCode,
                result.Data.FailReason,
                result.Data.NextAction
            );
            return result;
        }
        catch (JsonReaderException ex)
        {
            throw new JsonReaderException($"{ex.Message}\n {reply}");
        }
    }

    private async Task<HttpResponseMessage> SendSdoHttpRequestAsync
    (
        HttpMethod            method,
        string                endPoint,
        IReadOnlyList<string> paras,
        string?               tgt   = null,
        string                appId = SdoInfos.APP_ID
    )
    {
        using var request = GetSdoHttpRequestMessage(method, endPoint, paras, tgt, appId);
        return await loginHttpClient.SendAsync(request).ConfigureAwait(false);
    }

    private HttpRequestMessage GetSdoHttpRequestMessage
    (
        HttpMethod            method,
        string                endPoint,
        IReadOnlyList<string> paras,
        string?               tgt   = null,
        string                appId = SdoInfos.APP_ID
    )
    {
        var request = new HttpRequestMessage(method, BuildSdoRequestUri(endPoint, paras, appId));
        request.Headers.AddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.AddWithoutValidation
        (
            "User-Agent",
            "Mozilla/4.0 (compatible; MSIE 8.0; Windows NT 5.1; Trident/4.0; Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.1; SV1) ; InfoPath.2; .NET CLR 2.0.50727; MS-RTC LM 8; .NET CLR 3.0.04506.648; .NET CLR 3.5.21022; .NET CLR 1.1.4322; .NET CLR 3.0.4506.2152; .NET CLR 3.5.30729)"
        );
        request.Headers.AddWithoutValidation("Host", SdoInfos.GLOBAL_CAS_DOMAIN);

        if (endPoint is "ssoLogin.json" or "getPromotionInfo.json")
            request.Headers.AddWithoutValidation("Cookie", $"CASTGC={tgt}; CAS_LOGIN_STATE=1");

        var hasCid = loginCookies.GetAllCookies().Any(cookie => string.Equals(cookie.Name, "CASCID", StringComparison.OrdinalIgnoreCase));
        if (!hasCid)
            request.Headers.AddWithoutValidation("Cookie", $"CASCID={casCID}; SECURE_CASCID={casCID};");

        return request;
    }

    private Uri BuildSdoRequestUri(string endPoint, IReadOnlyList<string> paras, string appID)
    {
        var allParas = new List<string>(paras.Count + 20);
        allParas.AddRange(paras);
        allParas.AddRange
        (
            [
                "authenSource=1",
                $"appId={appID}",
                "areaId=1",
                $"appIdSite={appID}",
                "locale=zh_CN",
                "productId=4",
                "frameType=1",
                "endpointOS=1",
                "version=21",
                "customSecurityLevel=2",
                $"deviceId={deviceProfile.DeviceId}",
                "thirdLoginExtern=0",
                $"macId={deviceProfile.MacAddress}",
                "epIp=",
                $"epName={deviceProfile.HostName}",
                "extendInfo=",
                "sdoVersion=",
                "runTimeId=",
                "productVersion=1.9.7.10",
                "tag=0"
            ]
        );

        var casDomain   = Volatile.Read(ref casDomainMode) == 0 ? SdoInfos.GLOBAL_CAS_DOMAIN : SdoInfos.FALLBACK_CAS_DOMAIN;
        var requestPath = endPoint.StartsWith("/", StringComparison.Ordinal) ? endPoint : $"/authen/{endPoint}";
        var queryString = string.Join("&", allParas);

        return new UriBuilder(Uri.UriSchemeHttps, casDomain)
        {
            Path  = requestPath,
            Query = queryString
        }.Uri;
    }

    private Uri BuildCaptchaUri(string captchaUrl)
    {
        if (Uri.TryCreate(captchaUrl, UriKind.Absolute, out var absoluteUri))
            return absoluteUri;

        var casDomain = Volatile.Read(ref casDomainMode) == 0 ? SdoInfos.GLOBAL_CAS_DOMAIN : SdoInfos.FALLBACK_CAS_DOMAIN;
        var requestPath = captchaUrl.StartsWith('/')
                              ? captchaUrl
                              : captchaUrl.StartsWith("authen/", StringComparison.Ordinal)
                                  ? $"/{captchaUrl}"
                                  : $"/authen/{captchaUrl}";

        return new UriBuilder(Uri.UriSchemeHttps, casDomain)
        {
            Path = requestPath
        }.Uri;
    }

    private async Task<LoginResponse> GetJsonAsSdoClient
    (
        string                endPoint,
        IReadOnlyList<string> paras,
        string?               tgt   = null,
        string                appId = SdoInfos.APP_ID
    )
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var response = await SendSdoHttpRequestAsync(HttpMethod.Get, endPoint, paras, tgt, appId).ConfigureAwait(false);
                var       reply    = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return DeserializeLoginResponse(endPoint, reply);
            }
            catch (Exception ex) when (attempt == 0 && TrySwitchToFallbackDomain(ex))
            {
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        throw lastException ?? new InvalidOperationException("Failed to request SDO login endpoint");
    }

    private bool TrySwitchToFallbackDomain(Exception ex)
    {
        if (Interlocked.CompareExchange(ref casDomainMode, 1, 0) != 0)
            return false;

        Log.Error(ex, "[LoginChannelContext] GetJsonAsSdoClient 发生异常，切换备用域名");
        return true;
    }

    private async Task<string> SsoLoginAsync(string tgt, string guid)
    {
        var result = await GetJsonAsSdoClient("ssoLogin.json", [$"tgt={tgt}", $"guid={guid}"], tgt).ConfigureAwait(false);
        if (result.ReturnCode != 0)
            throw new LoginException(result.ReturnCode, result.Data.FailReason);

        return result.Data.Ticket;
    }

    private async Task<LoginResponse> GetPromotionInfoAsync(string tgt, string? serviceUrl = null)
    {
        var paras = new List<string> { $"tgt={tgt}" };
        if (serviceUrl != null)
            paras.Add($"serviceUrl={serviceUrl}");

        var result = await GetJsonAsSdoClient("getPromotionInfo.json", paras, tgt).ConfigureAwait(false);
        if (result.ReturnCode != 0)
            throw new LoginException(result.ReturnCode, result.Data.FailReason);

        return result;
    }
}
