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

    /// <summary>账号所属登录渠道</summary>
    public XIVAccountType AccountType { get; set; }

    /// <summary>盛趣渠道登录账号, 仅盛趣渠道使用</summary>
    [Column("LoginAccount")]
    public string SdoLoginAccount { get; set; } = null!;

    /// <summary>WeGame 登录账号, 仅 WeGame 渠道使用</summary>
    [Column("WeGameLoginAccount")]
    public string WeGameLoginAccount { get; set; } = null!;

    /// <summary>界面和本地查找使用的账号名, 会根据渠道切换到底层字段</summary>
    [Ignore]
    public string UserName => AccountType == XIVAccountType.Sdo ? SdoLoginAccount : WeGameLoginAccount;

    /// <summary>界面展示名, 优先显示用户自定义名</summary>
    [Ignore]
    public string DisplayName => string.IsNullOrWhiteSpace(UserDefinedName) ? UserName : UserDefinedName;

    /// <summary>用户自定义备注名, 不参与登录</summary>
    public string UserDefinedName { get; set; } = null!;

    #endregion

    #region 登录凭据

    /// <summary>当前选择的大区名</summary>
    public string AreaName { get; set; } = null!;

    /// <summary>是否启用快速登录</summary>
    public bool QuickLoginEnabled { get; set; } = true;

    /// <summary>盛趣快速登录凭据, 仅盛趣渠道使用</summary>
    [Column("AutoLoginSessionKey")]
    public string? SdoQuickLoginSecret { get; set; }

    /// <summary>盛趣静态密码, 仅盛趣密码登录使用</summary>
    [Column("Password")]
    public string? SdoPassword { get; set; }

    /// <summary>WeGame 快速登录凭据, 仅 WeGame 渠道使用</summary>
    [Column("WeGameTokenSecret")]
    public string? WeGameQuickLoginSecret { get; set; }

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
