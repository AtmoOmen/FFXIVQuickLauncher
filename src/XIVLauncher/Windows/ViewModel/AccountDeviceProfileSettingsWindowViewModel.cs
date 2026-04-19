using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using XIVLauncher.Accounts;
using XIVLauncher.Common.Game.Login;

namespace XIVLauncher.Windows.ViewModel;

internal sealed partial class AccountDeviceProfileSettingsWindowViewModel
(
    AccountManager accountManager
) : ViewModelBase
{
    private XIVAccount?             account;
    private DeviceProfileSnapshot?  savedSnapshot;
    private bool                    snapshotTouched;
    private bool                    isSharedMode;
    private string                  accountDisplayName = string.Empty;
    private string                  descriptionText = string.Empty;
    private string                  deviceDetailsHintText = string.Empty;
    private bool                    dynamicEnabled;
    private bool                    periodicRefreshEnabled;
    private int                     rotationDays = AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS;
    private string                  deviceId = string.Empty;
    private string                  macAddress = string.Empty;
    private string                  hostName = string.Empty;
    private string                  lastGeneratedText = string.Empty;
    private string                  nextRotationTimeText = string.Empty;
    private string                  remainingRotationTimeText = string.Empty;
    private string                  rotationSummaryText = string.Empty;

    public string MacHash =>
        TryNormalizeMacAddress(MacAddress, out var normalizedValue, out _)
            ? FakeMachineInfo.GetMacHash(normalizedValue)
            : string.Empty;

    public string CasCid =>
        TryNormalizeMacAddress(MacAddress, out var normalizedValue, out _)
            ? FakeMachineInfo.GetCasCid(normalizedValue)
            : string.Empty;

    public bool IsSharedMode => isSharedMode;

    public bool IsAccountMode => !isSharedMode;

    public string WindowTitle =>
        IsSharedMode ? "共享设备信息设置" : "账号设备信息设置";

    public string ProfileKindText =>
        IsSharedMode ? "共享设备信息" : "账号设备信息";

    public Visibility AccountModeOptionsVisibility =>
        IsSharedMode ? Visibility.Collapsed : Visibility.Visible;

    public bool CanEditRotationDays => DynamicEnabled && PeriodicRefreshEnabled;

    public bool CanShowPeriodicRefreshInfo => DynamicEnabled && PeriodicRefreshEnabled;

    public bool CanEditDeviceDetails => IsSharedMode || DynamicEnabled;

    public long GeneratedUtcTicks { get; private set; }

    public string AccountDisplayName
    {
        get => accountDisplayName;
        private set => SetProperty(ref accountDisplayName, value);
    }

    public string DescriptionText
    {
        get => descriptionText;
        private set => SetProperty(ref descriptionText, value);
    }

    public string DeviceDetailsHintText
    {
        get => deviceDetailsHintText;
        private set => SetProperty(ref deviceDetailsHintText, value);
    }

    public bool DynamicEnabled
    {
        get => dynamicEnabled;
        set
        {
            if (!SetProperty(ref dynamicEnabled, value))
                return;

            OnPropertyChanged(nameof(CanEditRotationDays));
            OnPropertyChanged(nameof(CanShowPeriodicRefreshInfo));
            OnPropertyChanged(nameof(CanEditDeviceDetails));
            UpdateRotationSummary();
        }
    }

    public bool PeriodicRefreshEnabled
    {
        get => periodicRefreshEnabled;
        set
        {
            if (!SetProperty(ref periodicRefreshEnabled, value))
                return;

            OnPropertyChanged(nameof(CanEditRotationDays));
            OnPropertyChanged(nameof(CanShowPeriodicRefreshInfo));
            UpdateRotationSummary();
        }
    }

    public int RotationDays
    {
        get => rotationDays;
        set
        {
            var normalizedValue = AccountManager.NormalizeDeviceProfileRotationDays(value);
            if (!SetProperty(ref rotationDays, normalizedValue))
                return;

            UpdateRotationSummary();
        }
    }

    public string DeviceId
    {
        get => deviceId;
        set => SetProperty(ref deviceId, value);
    }

    public string MacAddress
    {
        get => macAddress;
        set
        {
            if (!SetProperty(ref macAddress, value))
                return;

            OnPropertyChanged(nameof(MacHash));
            OnPropertyChanged(nameof(CasCid));
        }
    }

    public string HostName
    {
        get => hostName;
        set => SetProperty(ref hostName, value);
    }

    public string LastGeneratedText
    {
        get => lastGeneratedText;
        private set => SetProperty(ref lastGeneratedText, value);
    }

    public string NextRotationTimeText
    {
        get => nextRotationTimeText;
        private set => SetProperty(ref nextRotationTimeText, value);
    }

    public string RemainingRotationTimeText
    {
        get => remainingRotationTimeText;
        private set => SetProperty(ref remainingRotationTimeText, value);
    }

    public string RotationSummaryText
    {
        get => rotationSummaryText;
        private set => SetProperty(ref rotationSummaryText, value);
    }

    public void Load(XIVAccount targetAccount)
    {
        account = accountManager.Accounts.First(existing => existing.Id == targetAccount.Id);

        var persistedSnapshot = accountManager.TryGetSavedDeviceProfileSnapshot(account);
        var snapshot = account.DeviceProfileDynamicEnabled
                           ? persistedSnapshot ?? accountManager.GetEditableDeviceProfileSnapshot(account)
                           : accountManager.GetEditableDeviceProfileSnapshot(account);

        ApplyMode(false);

        savedSnapshot     = persistedSnapshot;
        GeneratedUtcTicks = account.DeviceProfileDynamicEnabled
                                ? account.DeviceProfileLastGeneratedUtcTicks
                                : accountManager.GetSharedDeviceProfileGeneratedUtcTicks();
        snapshotTouched = false;

        AccountDisplayName     = account.DisplayName;
        DescriptionText        = "账号登录时，盛趣会要求上传一系列设备标识。为进一步保护个人隐私，可以选择单独配置当前账号使用的设备信息。";
        DeviceDetailsHintText  = "可以直接修改下方输入框，或随机生成一整套新的仿真设备信息。";
        DynamicEnabled         = account.DeviceProfileDynamicEnabled;
        PeriodicRefreshEnabled = account.IsDeviceProfileRotation;
        RotationDays           = AccountManager.NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays);

        ApplySnapshot(snapshot);
        UpdateRotationSummary();
    }

    public void LoadShared()
    {
        account = null;

        ApplyMode(true);

        var snapshot = accountManager.GetSharedDeviceProfileSnapshot();

        savedSnapshot           = snapshot;
        GeneratedUtcTicks       = accountManager.GetSharedDeviceProfileGeneratedUtcTicks();
        snapshotTouched         = false;
        AccountDisplayName      = "共享设备信息";
        DescriptionText         = "未启用账号独立设备信息的账号登录时，会统一使用这套共享设备信息。可以在这里直接修改或刷新它。";
        DeviceDetailsHintText   = "可以直接修改下方输入框，或随机生成一整套新的共享设备信息。";
        DynamicEnabled          = false;
        PeriodicRefreshEnabled  = false;
        RotationDays            = AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS;

        ApplySnapshot(snapshot);
        UpdateRotationSummary();
    }

    public void Save()
    {
        var snapshot = CreateValidatedSnapshot();

        if (IsSharedMode)
        {
            if (HasSnapshotChanged(snapshot))
                GeneratedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;

            accountManager.SaveSharedDeviceProfileSnapshot(snapshot, GeneratedUtcTicks);
            savedSnapshot   = snapshot;
            snapshotTouched = false;

            ApplySnapshot(snapshot);
            UpdateRotationSummary();
            return;
        }

        var targetAccount = account ?? throw new InvalidOperationException("当前未加载账号设备信息。");

        if (DynamicEnabled && !targetAccount.DeviceProfileDynamicEnabled && !snapshotTouched && savedSnapshot != null)
            snapshot = savedSnapshot;

        var shouldPersistSnapshot = DynamicEnabled || snapshotTouched;

        if (shouldPersistSnapshot && HasSnapshotChanged(snapshot))
            GeneratedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;

        accountManager.UpdateDeviceProfileSettings
        (
            targetAccount,
            DynamicEnabled,
            PeriodicRefreshEnabled,
            RotationDays
        );

        if (shouldPersistSnapshot)
        {
            accountManager.SaveDeviceProfileSnapshot(targetAccount, snapshot, GeneratedUtcTicks);
            savedSnapshot = snapshot;
        }

        ApplySnapshot(snapshot);
        snapshotTouched = false;
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

        if (!IsSharedMode)
        {
            DynamicEnabled         = dynamicEnabled;
            PeriodicRefreshEnabled = periodicRefreshEnabled;
            RotationDays           = AccountManager.NormalizeDeviceProfileRotationDays(rotationDays);
        }

        DeviceId         = normalizedDeviceId;
        MacAddress       = normalizedMacAddress;
        HostName         = normalizedHostName;
        GeneratedUtcTicks = generatedUtcTicks > 0 ? generatedUtcTicks : DateTimeOffset.UtcNow.UtcTicks;
        snapshotTouched   = true;
        UpdateRotationSummary();
    }

    public void RefreshAll()
    {
        ApplySnapshot(FakeMachineInfo.CreateSnapshot());
        TouchGeneratedTime();
    }

    public void RefreshDeviceId()
    {
        RebuildDeviceIdFromCurrentFields();
        TouchGeneratedTime();
    }

    public void RefreshMacAddress()
    {
        MacAddress = FakeMachineInfo.CreateMacAddress();
        RebuildDeviceIdFromCurrentFields();
        TouchGeneratedTime();
    }

    public void RefreshHostName()
    {
        HostName = FakeMachineInfo.CreateHostName();
        RebuildDeviceIdFromCurrentFields();
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
        RebuildDeviceIdFromCurrentFields();
        TouchGeneratedTime();
        return true;
    }

    public bool TrySetHostName(string input, out string errorMessage)
    {
        if (!TryNormalizeHostName(input, out var normalizedValue, out errorMessage))
            return false;

        HostName = normalizedValue;
        RebuildDeviceIdFromCurrentFields();
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
            errorMessage = "设备 ID 必须是 “32 位十六进制:32 位十六进制:32 位十六进制” 的格式。";
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
            errorMessage    = "MAC 地址必须是 6 组十六进制字节，例如 “3C-52-82-1A-2B-3C”。";
            return false;
        }

        var bytes = segments
                    .Select(segment => byte.Parse(segment, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                    .ToArray();

        var isMulticast = (bytes[0] & 0x01) != 0;
        var isAllZero   = bytes.All(static b => b == 0x00);
        var isBroadcast = bytes.All(static b => b == 0xFF);

        if (isMulticast || isAllZero || isBroadcast)
        {
            normalizedValue = string.Empty;
            errorMessage    = "MAC 地址必须是单播地址，且不能为全 00 或全 FF。";
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

    private void ApplyMode(bool sharedMode)
    {
        if (isSharedMode == sharedMode)
            return;

        isSharedMode = sharedMode;

        OnPropertyChanged(nameof(IsSharedMode));
        OnPropertyChanged(nameof(IsAccountMode));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ProfileKindText));
        OnPropertyChanged(nameof(AccountModeOptionsVisibility));
        OnPropertyChanged(nameof(CanEditDeviceDetails));
    }

    private void ApplySnapshot(DeviceProfileSnapshot snapshot)
    {
        DeviceId   = snapshot.DeviceId;
        MacAddress = snapshot.MacAddress;
        HostName   = snapshot.HostName;
    }

    private void RebuildDeviceIdFromCurrentFields()
    {
        if (!TryNormalizeMacAddress(MacAddress, out var normalizedMacAddress, out _)
            || !TryNormalizeHostName(HostName, out var normalizedHostName, out _))
        {
            DeviceId = FakeMachineInfo.CreateDeviceId();
            return;
        }

        DeviceId = FakeMachineInfo.CreateDeviceId(normalizedMacAddress, normalizedHostName);
    }

    private DeviceProfileSnapshot CreateValidatedSnapshot()
    {
        if (!TryNormalizeDeviceId(DeviceId, out var normalizedDeviceId, out var deviceIdError))
            throw new InvalidOperationException(deviceIdError);

        if (!TryNormalizeMacAddress(MacAddress, out var normalizedMacAddress, out var macAddressError))
            throw new InvalidOperationException(macAddressError);

        if (!TryNormalizeHostName(HostName, out var normalizedHostName, out var hostNameError))
            throw new InvalidOperationException(hostNameError);

        return new DeviceProfileSnapshot
        {
            DeviceId   = normalizedDeviceId,
            MacAddress = normalizedMacAddress,
            HostName   = normalizedHostName
        };
    }

    private bool HasSnapshotChanged(DeviceProfileSnapshot snapshot)
    {
        if (savedSnapshot == null)
            return true;

        return !string.Equals(savedSnapshot.DeviceId, snapshot.DeviceId, StringComparison.Ordinal)
               || !string.Equals(savedSnapshot.MacAddress, snapshot.MacAddress, StringComparison.Ordinal)
               || !string.Equals(savedSnapshot.HostName, snapshot.HostName, StringComparison.Ordinal);
    }

    private void TouchGeneratedTime()
    {
        GeneratedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        snapshotTouched   = true;
        UpdateRotationSummary();
    }

    private void UpdateRotationSummary()
    {
        if (GeneratedUtcTicks <= 0)
        {
            LastGeneratedText = "最近生成时间：尚未生成";

            if (IsSharedMode)
            {
                NextRotationTimeText      = "自动轮换：共享设备信息尚未生成";
                RemainingRotationTimeText = "当前状态：尚未生成";
                RotationSummaryText       = "当前共享设备信息尚未生成，保存后会开始供未启用账号独立设备信息的账号共用。";
                return;
            }

            NextRotationTimeText      = "下次自动更换：尚未生成";
            RemainingRotationTimeText = "当前状态：尚未生成";
            RotationSummaryText = DynamicEnabled
                                      ? "当前账号尚未生成独立设备信息，保存后将开始使用当前这套仿真设备信息。"
                                      : "当前未启用账号独立设备信息，登录时将使用共享设备信息。";
            return;
        }

        var generatedAtLocal = new DateTimeOffset(GeneratedUtcTicks, TimeSpan.Zero).ToLocalTime();
        LastGeneratedText = $"最近生成时间：{generatedAtLocal:yyyy-MM-dd HH:mm:ss}";

        if (IsSharedMode)
        {
            NextRotationTimeText      = "自动轮换：共享设备信息不会自动轮换";
            RemainingRotationTimeText = "当前状态：未启用账号独立设备信息的账号会共用这套设备信息";
            RotationSummaryText       = "当前正在使用这套共享设备信息。可以直接修改，或手动刷新生成一套新的共享设备信息。";
            return;
        }

        if (!DynamicEnabled)
        {
            NextRotationTimeText      = "下次自动更换：未启用账号独立设备信息";
            RemainingRotationTimeText = "当前状态：正在使用共享设备信息";
            RotationSummaryText       = "当前未启用账号独立设备信息，登录时将使用共享设备信息。";
            return;
        }

        if (!PeriodicRefreshEnabled)
        {
            NextRotationTimeText      = "下次自动更换：未启用定期自动更换";
            RemainingRotationTimeText = "当前状态：需要手动刷新";
            RotationSummaryText       = "已启用账号独立设备信息，当前会固定使用这套仿真设备信息。";
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
