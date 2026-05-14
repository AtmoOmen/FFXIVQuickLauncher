using System;
using SQLite;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Account;

public class XIVAccount : IEquatable<XIVAccount>
{
    #region 身份信息

    /// <summary>是否属于 WeGame 渠道</summary>
    [Ignore]
    public bool IsWeGame => AccountType == XIVAccountType.WeGame;

    /// <summary>本地主键, 旧库列名保留为 index</summary>
    [Unique]
    [AutoIncrement]
    [PrimaryKey]
    [Column("index")]
    public int Index { get; set; }

    /// <summary>本地账号唯一标识, 旧库列名保留为 Id</summary>
    [Column("Id")]
    public string ID { get; set; } = null!;

    /// <summary>盛趣渠道登录账号, 用于密码登录和一键登录的本地查找</summary>
    [Column("LoginAccount")]
    public string SdoLoginAccount { get; set; } = null!;

    /// <summary>WeGame 返回的 SndaID, 用于 WeGame 渠道的本地查找与展示</summary>
    [Column("SndaId")]
    public string WeGameSndaID { get; set; } = null!;

    /// <summary>界面展示名, 优先显示用户自定义名</summary>
    [Ignore]
    public string DisplayName => string.IsNullOrWhiteSpace(UserDefinedName) ? UserName : UserDefinedName;

    /// <summary>界面和查找使用的账号名, 盛趣使用登录账号, WeGame 使用 SndaID</summary>
    [Ignore]
    public string UserName => AccountType == XIVAccountType.Sdo ? SdoLoginAccount : WeGameSndaID;

    /// <summary>用户自定义备注名, 不参与登录</summary>
    public string UserDefinedName { get; set; } = null!;

    /// <summary>账号所属登录渠道</summary>
    public XIVAccountType AccountType { get; set; }

    #endregion

    #region 登录凭据

    /// <summary>当前选择的大区名</summary>
    public string AreaName { get; set; } = null!;

    /// <summary>是否启用自动登录</summary>
    public bool AutoLogin { get; set; }

    /// <summary>盛趣自动登录会话密钥, 仅一键登录使用</summary>
    [Column("AutoLoginSessionKey")]
    public string? SdoAutoLoginSessionKey { get; set; }

    /// <summary>盛趣静态密码, 仅静态密码登录使用</summary>
    [Column("Password")]
    public string? SdoPassword { get; set; }

    /// <summary>WeGame 手动抓包令牌, 仅 WeGame 手动抓包渠道使用</summary>
    public string? WeGameTokenSecret { get; set; }

    /// <summary>WeGame SID, 仅 WeGame 的自动读取登录渠道使用</summary>
    [Column("TestSID")]
    public string? WeGameSIDSecret { get; set; }

    /// <summary>游戏会话 ID, 由自动读取登录渠道刷新</summary>
    [Column("NSessionId")]
    public string? WeGameSessionID { get; set; }

    #endregion

    #region 设备信息

    public string DeviceProfileDeviceId { get; set; } = string.Empty;

    public string DeviceProfileMacAddress { get; set; } = string.Empty;

    public string DeviceProfileHostName { get; set; } = string.Empty;

    public string DeviceProfilePresetId { get; set; } = string.Empty;

    public bool DeviceProfileDynamicEnabled { get; set; }

    public bool IsDeviceProfileRotation { get; set; }

    public int DeviceProfileRotationDays { get; set; } = AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS;

    public long DeviceProfileLastGeneratedUtcTicks { get; set; }

    #endregion

    public void GenerateID() =>
        ID = $"{UserName}|{AccountType}";

    public override int GetHashCode() =>
        HashCode.Combine(UserName, AccountType);

    public bool Equals(XIVAccount? other) =>
        other is not null && string.Equals(UserName, other.UserName, StringComparison.Ordinal) && AccountType == other.AccountType;

    public override bool Equals(object? obj) =>
        obj is XIVAccount other && Equals(other);
}
