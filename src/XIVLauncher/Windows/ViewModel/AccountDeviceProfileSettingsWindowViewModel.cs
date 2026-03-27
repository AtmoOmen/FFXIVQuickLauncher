using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using XIVLauncher.Accounts;
using XIVLauncher.Common.Game.Login;

namespace XIVLauncher.Windows.ViewModel;

internal sealed partial class AccountDeviceProfileSettingsWindowViewModel
(
    AccountManager accountManager
) : ViewModelBase
{
    public string MacHash =>
        TryNormalizeMacAddress(MacAddress, out var normalizedValue, out _)
            ? FakeMachineInfo.GetMacHash(normalizedValue)
            : string.Empty;

    public string CasCid =>
        TryNormalizeMacAddress(MacAddress, out var normalizedValue, out _)
            ? FakeMachineInfo.GetCasCid(normalizedValue)
            : string.Empty;

    public bool CanEditRotationDays => DynamicEnabled && PeriodicRefreshEnabled;

    public bool CanShowPeriodicRefreshInfo => DynamicEnabled && PeriodicRefreshEnabled;

    public long GeneratedUtcTicks { get; private set; }

    public string AccountDisplayName
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public bool DynamicEnabled
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnPropertyChanged(nameof(CanEditRotationDays));
            OnPropertyChanged(nameof(CanShowPeriodicRefreshInfo));
            UpdateRotationSummary();
        }
    }

    public bool PeriodicRefreshEnabled
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnPropertyChanged(nameof(CanEditRotationDays));
            OnPropertyChanged(nameof(CanShowPeriodicRefreshInfo));
            UpdateRotationSummary();
        }
    }

    public int RotationDays
    {
        get;
        set
        {
            var normalizedValue = AccountManager.NormalizeDeviceProfileRotationDays(value);
            if (!SetProperty(ref field, normalizedValue))
                return;

            UpdateRotationSummary();
        }
    } = AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS;

    public string DeviceId
    {
        get => field;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string MacAddress
    {
        get => field;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnPropertyChanged(nameof(MacHash));
            OnPropertyChanged(nameof(CasCid));
        }
    } = string.Empty;

    public string HostName
    {
        get => field;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string LastGeneratedText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public string NextRotationTimeText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public string RemainingRotationTimeText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public string RotationSummaryText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    private XIVAccount             _account = null!;
    private DeviceProfileSnapshot? _savedSnapshot;
    private bool                   _snapshotTouched;

    public void Load(XIVAccount account)
    {
        _account = accountManager.Accounts.First(existing => existing.Id == account.Id);

        var savedSnapshot = accountManager.TryGetSavedDeviceProfileSnapshot(_account);
        var snapshot = _account.DeviceProfileDynamicEnabled
                           ? savedSnapshot ?? accountManager.GetEditableDeviceProfileSnapshot(_account)
                           : accountManager.GetEditableDeviceProfileSnapshot(_account);

        _savedSnapshot = savedSnapshot;
        GeneratedUtcTicks = _account.DeviceProfileDynamicEnabled
                                ? _account.DeviceProfileLastGeneratedUtcTicks
                                : accountManager.GetSharedDeviceProfileGeneratedUtcTicks();
        _snapshotTouched = false;

        AccountDisplayName     = _account.DisplayName;
        DynamicEnabled         = _account.DeviceProfileDynamicEnabled;
        PeriodicRefreshEnabled = _account.IsDeviceProfileRotation;
        RotationDays           = AccountManager.NormalizeDeviceProfileRotationDays(_account.DeviceProfileRotationDays);

        ApplySnapshot(snapshot);
        UpdateRotationSummary();
    }

    public void Save()
    {
        var snapshot = CreateValidatedSnapshot();
        if (DynamicEnabled && !_account.DeviceProfileDynamicEnabled && !_snapshotTouched && _savedSnapshot != null)
            snapshot = _savedSnapshot;

        var shouldPersistSnapshot = DynamicEnabled || _snapshotTouched;

        if (shouldPersistSnapshot && HasSnapshotChanged(snapshot))
            GeneratedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;

        accountManager.UpdateDeviceProfileSettings
        (
            _account,
            DynamicEnabled,
            PeriodicRefreshEnabled,
            RotationDays
        );

        if (shouldPersistSnapshot)
        {
            accountManager.SaveDeviceProfileSnapshot(_account, snapshot, GeneratedUtcTicks);
            _savedSnapshot = snapshot;
        }

        ApplySnapshot(snapshot);
        _snapshotTouched = false;
        UpdateRotationSummary();
    }

    public void Import
    (
        bool   dynamicEnabled,
        bool   periodicRefreshEnabled,
        int    rotationDays,
        string deviceId,
        string macAddress,
        string hostName,
        long   generatedUtcTicks
    )
    {
        if (!TryNormalizeDeviceId(deviceId, out var normalizedDeviceId, out var deviceIdError))
            throw new InvalidOperationException(deviceIdError);

        if (!TryNormalizeMacAddress(macAddress, out var normalizedMacAddress, out var macAddressError))
            throw new InvalidOperationException(macAddressError);

        if (!TryNormalizeHostName(hostName, out var normalizedHostName, out var hostNameError))
            throw new InvalidOperationException(hostNameError);

        DynamicEnabled         = dynamicEnabled;
        PeriodicRefreshEnabled = periodicRefreshEnabled;
        RotationDays           = AccountManager.NormalizeDeviceProfileRotationDays(rotationDays);
        DeviceId               = normalizedDeviceId;
        MacAddress             = normalizedMacAddress;
        HostName               = normalizedHostName;
        GeneratedUtcTicks      = generatedUtcTicks > 0 ? generatedUtcTicks : DateTimeOffset.UtcNow.UtcTicks;
        _snapshotTouched       = true;
        UpdateRotationSummary();
    }

    public void RefreshAll()
    {
        ApplySnapshot(FakeMachineInfo.CreateSnapshot());
        TouchGeneratedTime();
    }

    public void RefreshDeviceId()
    {
        DeviceId = FakeMachineInfo.CreateDeviceId();
        TouchGeneratedTime();
    }

    public void RefreshMacAddress()
    {
        MacAddress = FakeMachineInfo.CreateMacAddress();
        TouchGeneratedTime();
    }

    public void RefreshHostName()
    {
        HostName = FakeMachineInfo.CreateHostName();
        TouchGeneratedTime();
    }

    public bool TrySetDeviceId(string input, out string errorMessage)
    {
        if (!TryNormalizeDeviceId(input, out var normalizedValue, out errorMessage))
            return false;

        DeviceId = normalizedValue;
        TouchGeneratedTime();
        errorMessage = string.Empty;
        return true;
    }

    public bool TrySetMacAddress(string input, out string errorMessage)
    {
        if (!TryNormalizeMacAddress(input, out var normalizedValue, out errorMessage))
            return false;

        MacAddress = normalizedValue;
        TouchGeneratedTime();
        return true;
    }

    public bool TrySetHostName(string input, out string errorMessage)
    {
        if (!TryNormalizeHostName(input, out var normalizedValue, out errorMessage))
            return false;

        HostName = normalizedValue;
        TouchGeneratedTime();
        return true;
    }

    private static string FormatRemainingTime(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "已到达更换时间";

        var totalHours = Math.Max(remaining.TotalHours, 0);
        var days       = remaining.Days;
        var hours      = remaining.Hours;
        var minutes    = remaining.Minutes;

        if (days > 0)
            return $"{days} 天 {hours} 小时 {minutes} 分钟";

        if (totalHours >= 1)
            return $"{hours} 小时 {minutes} 分钟";

        return $"{Math.Max(minutes, 1)} 分钟";
    }

    private static bool TryNormalizeDeviceId(string input, out string normalizedValue, out string errorMessage)
    {
        normalizedValue = input.Trim().ToUpperInvariant();

        if (!DeviceIdRegex().IsMatch(normalizedValue))
        {
            errorMessage = "设备 ID 必须是“32位十六进制:32位十六进制:32位十六进制”的格式。";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private static bool TryNormalizeMacAddress(string input, out string normalizedValue, out string errorMessage)
    {
        var rawValue = input.Trim().Replace(":", "-", StringComparison.Ordinal);
        var segments = rawValue.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length != 6 || segments.Any(segment => segment.Length != 2 || !byte.TryParse(segment, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)))
        {
            normalizedValue = string.Empty;
            errorMessage    = "MAC 地址必须是 6 组十六进制字节，例如“02-1A-2B-3C-4D-5E”。";
            return false;
        }

        var bytes = segments
                    .Select(segment => byte.Parse(segment, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                    .ToArray();

        var isMulticast    = (bytes[0] & 0x01) != 0;
        var isLocallyAdmin = (bytes[0] & 0x02) != 0;

        if (isMulticast || !isLocallyAdmin)
        {
            normalizedValue = string.Empty;
            errorMessage    = "MAC 地址必须是本地管理且非组播地址。";
            return false;
        }

        normalizedValue = string.Join("-", bytes.Select(static b => b.ToString("X2", CultureInfo.InvariantCulture)));
        errorMessage    = string.Empty;
        return true;
    }

    private static bool TryNormalizeHostName(string input, out string normalizedValue, out string errorMessage)
    {
        normalizedValue = input.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            errorMessage = "主机名不能为空。";
            return false;
        }

        if (normalizedValue.Length > 63)
        {
            errorMessage = "主机名长度不能超过 63 个字符。";
            return false;
        }

        if (normalizedValue.Any(char.IsWhiteSpace))
        {
            errorMessage = "主机名不能包含空白字符。";
            return false;
        }

        normalizedValue = normalizedValue.ToUpperInvariant();
        errorMessage    = string.Empty;
        return true;
    }

    [GeneratedRegex("^[0-9A-F]{32}:[0-9A-F]{32}:[0-9A-F]{32}$", RegexOptions.Compiled)]
    private static partial Regex DeviceIdRegex();

    private void ApplySnapshot(DeviceProfileSnapshot snapshot)
    {
        DeviceId   = snapshot.DeviceId;
        MacAddress = snapshot.MacAddress;
        HostName   = snapshot.HostName;
    }

    private DeviceProfileSnapshot CreateValidatedSnapshot()
    {
        if (!TryNormalizeDeviceId(DeviceId, out var deviceId, out var deviceIdError))
            throw new InvalidOperationException(deviceIdError);

        if (!TryNormalizeMacAddress(MacAddress, out var macAddress, out var macAddressError))
            throw new InvalidOperationException(macAddressError);

        if (!TryNormalizeHostName(HostName, out var hostName, out var hostNameError))
            throw new InvalidOperationException(hostNameError);

        return new DeviceProfileSnapshot
        {
            DeviceId   = deviceId,
            MacAddress = macAddress,
            HostName   = hostName
        };
    }

    private bool HasSnapshotChanged(DeviceProfileSnapshot snapshot)
    {
        var savedSnapshot = accountManager.TryGetSavedDeviceProfileSnapshot(_account);
        if (savedSnapshot == null)
            return true;

        return !string.Equals(savedSnapshot.DeviceId,      snapshot.DeviceId,   StringComparison.Ordinal)
               || !string.Equals(savedSnapshot.MacAddress, snapshot.MacAddress, StringComparison.Ordinal)
               || !string.Equals(savedSnapshot.HostName,   snapshot.HostName,   StringComparison.Ordinal);
    }

    private void TouchGeneratedTime()
    {
        GeneratedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        _snapshotTouched  = true;
        UpdateRotationSummary();
    }

    private void UpdateRotationSummary()
    {
        if (GeneratedUtcTicks <= 0)
        {
            LastGeneratedText         = "最近生成时间：尚未生成";
            NextRotationTimeText      = "下次自动更换：尚未生成";
            RemainingRotationTimeText = "当前状态：尚未生成";
            RotationSummaryText = DynamicEnabled
                                      ? "当前账号尚未生成独立设备信息，保存后将使用当前这套模拟设备信息。"
                                      : "当前未启用账号独立设备信息，登录时将使用内置共享设备信息。";
            return;
        }

        var generatedAtLocal = new DateTimeOffset(GeneratedUtcTicks, TimeSpan.Zero).ToLocalTime();
        LastGeneratedText = $"最近生成时间：{generatedAtLocal:yyyy-MM-dd HH:mm:ss}";

        if (!DynamicEnabled)
        {
            NextRotationTimeText      = "下次自动更换：未启用账号独立设备信息";
            RemainingRotationTimeText = "当前状态：正在使用共享设备信息";
            RotationSummaryText       = "当前未启用账号独立设备信息，登录时将使用内置共享设备信息。";
            return;
        }

        if (!PeriodicRefreshEnabled)
        {
            NextRotationTimeText      = "下次自动更换：未启用定期自动更换";
            RemainingRotationTimeText = "当前状态：需要手动刷新";
            RotationSummaryText       = "已启用账号独立设备信息，当前会固定使用这套模拟设备信息。";
            return;
        }

        var nextRotationLocal = generatedAtLocal.AddDays(RotationDays);
        var remaining         = nextRotationLocal - DateTimeOffset.Now;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        NextRotationTimeText      = $"下次自动更换：{nextRotationLocal:yyyy-MM-dd HH:mm:ss}";
        RemainingRotationTimeText = $"剩余时间：{FormatRemainingTime(remaining)}";
        RotationSummaryText       = $"已启用账号独立设备信息，并将每隔 {RotationDays} 天自动更换一次。";
    }
}
