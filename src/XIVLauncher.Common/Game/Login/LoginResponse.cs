using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Game.Login;

public class LoginResponse
{
    [JsonProperty("error_type")]
    public int ErrorType;

    [JsonProperty("return_code")]
    public int ReturnCode;

    [JsonProperty("data")]
    public LoginResponseData Data = null!;

    public class LoginResponseData
    {
        [JsonProperty("failReason")]
        public string FailReason = null!;

        [JsonProperty("nextAction")]
        public int NextAction;

        [JsonProperty("guid")]
        public string Guid = null!;

        [JsonProperty("pushMsgSerialNum")]
        public string PushMsgSerialNum = null!;

        [JsonProperty("pushMsgSessionKey")]
        public string PushMsgSessionKey = null!;

        [JsonProperty("dynamicKey")]
        public string DynamicKey = null!;

        [JsonConverter(typeof(MaskMiddleConverter))]
        [JsonProperty("ticket")]
        public string Ticket = null!;

        [JsonConverter(typeof(MaskMiddleConverter))]
        [JsonProperty("sndaId")]
        public string SndaID = null!;

        [JsonConverter(typeof(MaskMiddleConverter))]
        [JsonProperty("tgt")]
        public string Tgt = null!;

        [JsonConverter(typeof(MaskMiddleConverter))]
        [JsonProperty("autoLoginSessionKey")]
        public string AutoLoginSessionKey = null!;

        [JsonProperty("autoLoginMaxAge")]
        public int AutoLoginMaxAge;

        [JsonConverter(typeof(MaskMiddleConverter))]
        [JsonProperty("inputUserId")]
        public string InputUserID = null!;

        [JsonConverter(typeof(MaskMiddleConverter))]
        [JsonProperty("accountArray")]
        public List<string> AccountArray = null!;

        [JsonConverter(typeof(MaskMiddleConverter))]
        [JsonProperty("sndaIdArray")]
        public List<string> SndaIDArray = null!;

        [JsonProperty("flowId")]
        public string? FlowId;

        [JsonProperty("mobileMask")]
        public string? MobileMask;

        [JsonProperty("safePhoneTip")]
        public string? SafePhoneTip;

        [JsonProperty("picUrl")]
        public string? PicUrl;

        [JsonProperty("checkCodeUrl")]
        public string? CheckCodeUrl;

        [JsonProperty("gt_url")]
        public string? GtUrl;

        [JsonProperty("sdg_height")]
        public int? SdgHeight;

        [JsonProperty("sdg_width")]
        public int? SdgWidth;

        [JsonProperty("width")]
        public int? Width;

        [JsonProperty("height")]
        public int? Height;

        [JsonProperty("captchaParams")]
        public JToken? CaptchaParams;

        [JsonProperty("smsSessionKey")]
        public string? SmsSessionKey;

        [JsonProperty("checkCodeSessionKey")]
        public string? CheckCodeSessionKey;

        [JsonProperty("recommendLoginType")]
        public string? RecommendLoginType;

        [JsonProperty("hasPwdLoginRecord")]
        public string? HasPwdLoginRecord;

        [JsonProperty("hasCheckCodeLoginRecord")]
        public string? HasCheckCodeLoginRecord;

        [JsonProperty("bindPhoneStatus")]
        public string? BindPhoneStatus;

        [JsonProperty("ueFlowId")]
        public string? UeFlowId;
    }
}
