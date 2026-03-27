using System;
using SQLite;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Accounts;

public class XIVAccount : IEquatable<XIVAccount>
{
    [Ignore] public bool IsWeGame => AccountType != XIVAccountType.Sdo;

    [Unique] [AutoIncrement] [PrimaryKey] public int index { get; set; }

    public string Id { get; set; } = null!;

    public string SndaId { get; set; } = null!;

    [Ignore] public string DisplayName => string.IsNullOrWhiteSpace(UserDefinedName) ? UserName : UserDefinedName;

    [Ignore] public string UserName => AccountType is XIVAccountType.Sdo or XIVAccountType.WeGame ? LoginAccount : SndaId;

    public string         UserDefinedName { get; set; } = null!;
    public XIVAccountType AccountType     { get; set; }
    public string         LoginAccount    { get; set; } = null!;

    public string AreaName { get; set; } = null!;

    public bool AutoLogin { get; set; }

    public string  AutoLoginSessionKey { get; set; } = null!;
    public string  Password            { get; set; } = null!;
    public string? TestSID             { get; set; }
    public string  NSessionId          { get; set; } = null!;

    public string DeviceProfileDeviceId { get; set; } = string.Empty;

    public string DeviceProfileMacAddress { get; set; } = string.Empty;

    public string DeviceProfileHostName { get; set; } = string.Empty;

    public bool DeviceProfileDynamicEnabled { get; set; }

    public bool IsDeviceProfileRotation { get; set; }

    public int DeviceProfileRotationDays { get; set; } = AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS;

    public long DeviceProfileLastGeneratedUtcTicks { get; set; }

    public void GenerateID() =>
        Id = $"{UserName}|{AccountType}";

    public override int GetHashCode() =>
        HashCode.Combine(UserName, AccountType);

    public bool Equals(XIVAccount? other) =>
        other is not null && string.Equals(UserName, other.UserName, StringComparison.Ordinal) && AccountType == other.AccountType;

    public override bool Equals(object? obj) =>
        obj is XIVAccount other && Equals(other);
}
