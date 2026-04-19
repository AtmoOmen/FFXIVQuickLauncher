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
    private XIVAccount?            account;
    private DeviceProfileSnapshot? savedSnapshot;
    private bool                   snapshotTouched;

    public string MacHash =>
        TryNormalizeMacAddress(MacAddress, out var normalizedValue, out _)
            ? FakeMachineInfo.GetMacHash(normalizedValue)
            : string.Empty;

    public string CasCid =>
        TryNormalizeMacAddress(MacAddress, out var normalizedValue, out _)
            ? FakeMachineInfo.GetCasCid(normalizedValue)
            : string.Empty;

    public bool IsSharedMode { get; private set; }

    public bool IsAccountMode => !IsSharedMode;

    public string WindowTitle =>
        IsSharedMode ? "共享设备信息设置" : "账号设备信息设置";

    public string ProfileKindText =>
        IsSharedMode ? "共享设备信息" : "账号设备信息";

    public Visibility AccountModeOptionsVisibility =>
        IsSharedMode ? Visibility.Collapsed : Visibility.Visible;

    public Visibility AccountModeSectionVisibility =>
        IsSharedMode ? Visibility.Collapsed : Visibility.Visible;

    public bool CanEditRotationDays => DynamicEnabled && PeriodicRefreshEnabled;

    public bool CanEditDeviceDetails => IsSharedMode || DynamicEnabled;

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
            OnPropertyChanged(nameof(CanEditDeviceDetails));
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
        }
    } = AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS;

    public string DeviceId
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string MacAddress
    {
        get;
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
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public void Load(XIVAccount targetAccount)
    {
        account = accountManager.Accounts.First(existing => existing.Id == targetAccount.Id);

        var persistedSnapshot = accountManager.TryGetSavedDeviceProfileSnapshot(account);
        var snapshot = account.DeviceProfileDynamicEnabled
                           ? persistedSnapshot ?? accountManager.GetEditableDeviceProfileSnapshot(account)
                           : accountManager.GetEditableDeviceProfileSnapshot(account);

        ApplyMode(false);

        savedSnapshot = persistedSnapshot;
        GeneratedUtcTicks = account.DeviceProfileDynamicEnabled
                                ? account.DeviceProfileLastGeneratedUtcTicks
                                : accountManager.GetSharedDeviceProfileGeneratedUtcTicks();
        snapshotTouched = false;

        AccountDisplayName     = account.DisplayName;
        DynamicEnabled         = account.DeviceProfileDynamicEnabled;
        PeriodicRefreshEnabled = account.IsDeviceProfileRotation;
        RotationDays           = AccountManager.NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays);

        ApplySnapshot(snapshot);
    }

    public void LoadShared()
    {
        account = null;

        ApplyMode(true);

        var snapshot = accountManager.GetSharedDeviceProfileSnapshot();

        savedSnapshot          = snapshot;
        GeneratedUtcTicks      = accountManager.GetSharedDeviceProfileGeneratedUtcTicks();
        snapshotTouched        = false;
        AccountDisplayName     = "共享设备信息";
        DynamicEnabled         = false;
        PeriodicRefreshEnabled = false;
        RotationDays           = AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS;

        ApplySnapshot(snapshot);
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

        DeviceId          = normalizedDeviceId;
        MacAddress        = normalizedMacAddress;
        HostName          = normalizedHostName;
        GeneratedUtcTicks = generatedUtcTicks > 0 ? generatedUtcTicks : DateTimeOffset.UtcNow.UtcTicks;
        snapshotTouched   = true;
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
        if (IsSharedMode == sharedMode)
            return;

        IsSharedMode = sharedMode;

        OnPropertyChanged(nameof(IsSharedMode));
        OnPropertyChanged(nameof(IsAccountMode));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ProfileKindText));
        OnPropertyChanged(nameof(AccountModeOptionsVisibility));
        OnPropertyChanged(nameof(AccountModeSectionVisibility));
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

        return !string.Equals(savedSnapshot.DeviceId,      snapshot.DeviceId,   StringComparison.Ordinal)
               || !string.Equals(savedSnapshot.MacAddress, snapshot.MacAddress, StringComparison.Ordinal)
               || !string.Equals(savedSnapshot.HostName,   snapshot.HostName,   StringComparison.Ordinal);
    }

    private void TouchGeneratedTime()
    {
        GeneratedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        snapshotTouched   = true;
    }
}
