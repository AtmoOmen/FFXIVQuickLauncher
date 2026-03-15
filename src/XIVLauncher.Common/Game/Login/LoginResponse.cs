using System.Collections.Generic;
using Newtonsoft.Json;

namespace XIVLauncher.Common.Game.Login;

public class LoginResponse
{
    [JsonProperty("error_type")]
    public int ErrorType;

    [JsonProperty("return_code")]
    public int ReturnCode;

    [JsonProperty("data")]
    public LoginResponseData Data = null!;

    public string ToLog()
    {
        var settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };
        return JsonConvert.SerializeObject(this, Formatting.Indented, settings);
    }

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
    }
}
