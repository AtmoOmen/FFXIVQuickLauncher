using System;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SQLite;

namespace XIVLauncher.Accounts;

public enum XivAccountType
{
    Sdo,
    WeGame,
    WeGameSid
}

public class XivAccount : IEquatable<XivAccount>
{
    [Ignore] public bool IsWeGame => AccountType != XivAccountType.Sdo;
    //public string Id => $"{UserName}-{UseOtp}-{UseSteamServiceAccount}";

    //public override string ToString() => Id;

    /*
     * 目前有如下几种登录方式:
     * 盛趣账密     LoginAccount Password
     * 叨鱼扫码     LoginAccount AutoLoginSessionKey
     * 叨鱼滑动     LoginAccount AutoLoginSessionKey
     * WG手动抓包   ThirdLoginAccount  Token
     * WG抓SID     SndaId&AreaId SessionID
     *
     * SndaId XivAccountType:WeGame ThirdLoginAccount (AutoLoginSessionKey)
     * SndaId XivAccountType:WeGameSid AreaName (SessionID)
     * SndaId XivAccountType:Sdo LoginAccount (AutoLoginSessionKey Password)
     */

    [Unique] [AutoIncrement] [PrimaryKey] public int index { get; set; }

    public string Id { get; set; }

    public string SndaId { get; set; }

    // for Account Manager
    [Ignore] public string DisplayName
    {
        get
        {
            if (UserDefinedName is not null)
                return UserDefinedName;
            return UserName;
        }
        private set { }
    }

    // for Input Box
    [Ignore] public string UserName
    {
        get
        {
            if (AccountType == XivAccountType.Sdo || AccountType == XivAccountType.WeGame)
                return LoginAccount;
            return SndaId;
        }
        private set { }
    }

    public string         UserDefinedName { get; set; }
    public XivAccountType AccountType     { get; set; }
    public string         LoginAccount    { get; set; }

    public string AreaName { get; set; }

    public bool AutoLogin { get; set; }

    // Should be encrypted
    public string AutoLoginSessionKey { get; set; }
    public string Password            { get; set; }
    public string TestSID             { get; set; }
    public string NSessionId          { get; set; }

    [Ignore] public string ThumbnailUrl { get; set; }

    [Ignore] public string ChosenCharacterName { get; set; }

    [Ignore] public string ChosenCharacterWorld { get; set; }

    private const string URL = "https://xivapi.com/";

    public static XivAccount CreateAccount(XivAccountType accountType, string sndaId, string account = null, string areaName = null, string sessionId = null)
    {
        var newAccount = new XivAccount();
        Debug.Assert(sndaId != null);
        newAccount.AccountType = accountType;
        newAccount.SndaId      = sndaId;

        switch (accountType)
        {
            case XivAccountType.WeGameSid:
                Debug.Assert(areaName != null);
                Debug.Assert(account  == null);
                newAccount.SndaId   = sndaId;
                newAccount.AreaName = areaName;
                newAccount.TestSID  = sessionId;
                break;

            case XivAccountType.Sdo:
            case XivAccountType.WeGame:
                Debug.Assert(account != null);
                newAccount.LoginAccount = account;
                break;
        }

        newAccount.GenerateId();
        return newAccount;
    }

    public static async Task<JObject> GetCharacterSearch(string name, string world) =>
        await Get("character/search" + $"?name={name}&server={world}");

    public static async Task<dynamic> Get(string endpoint)
    {
        using var client = new WebClient();

        var result = await client.DownloadStringTaskAsync(URL + endpoint);

        var parsedObject = JObject.Parse(result);

        return parsedObject;
    }

    public void GenerateId() =>
        Id = $"{UserName}|{AccountType}";

    public override int GetHashCode() =>
        (UserName, AccountType).GetHashCode();

    public bool Equals(XivAccount other) =>
        GetHashCode() == other.GetHashCode();

    public string FindCharacterThumb() =>
        null;
}
