using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using XIVLauncher.Accounts;
using XIVLauncher.Accounts.DeviceProfiles;
using XIVLauncher.Common.Game.Login;

namespace XIVLauncher.Windows.ViewModel;

internal sealed class AccountDeviceProfileWindowViewModel
(
    AccountManager accountManager
) : INotifyPropertyChanged
{
    private static readonly Regex DeviceIdPattern = new
    (
        "^[0-9A-F]{32}:[0-9A-F]{32}:[0-9A-F]{32}$",
        RegexOptions.Compiled
    );

    private XIVAccount?            account;
    private DeviceProfilePreset?   savedIndependentPreset;
    private DeviceProfileSnapshot? selectedPresetSnapshot;
    private bool                   isApplyingPresetSelection;
    private bool                   isApplyingSnapshot;
    private bool                   snapshotTouched;
    private bool                   persistChangesToAccountManager;

    public ObservableCollection<DeviceProfilePreset> Presets { get; } = [];

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

    public bool CanSelectPreset => IsSharedMode || DynamicEnabled;

    public long GeneratedUtcTicks { get; private set; }

    public string AccountDisplayName
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public string PresetRemark
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string SelectedPresetId
    {
        get;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (!SetProperty(ref field, normalizedValue))
                return;

            if (isApplyingPresetSelection)
                return;

            ApplySelectedPreset(normalizedValue);
        }
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
            OnPropertyChanged(nameof(CanSelectPreset));
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
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnSnapshotFieldsChanged();
        }
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
            OnSnapshotFieldsChanged();
        }
    } = string.Empty;

    public string HostName
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnSnapshotFieldsChanged();
        }
    } = string.Empty;

    public void Load(XIVAccount targetAccount)
    {
        account                        = accountManager.Accounts.First(existing => existing.ID == targetAccount.ID);
        persistChangesToAccountManager = true;

        ApplyMode(false);
        LoadPresets();

        var sharedPreset = GetRequiredSharedPreset();
        savedIndependentPreset = accountManager.FindDeviceProfilePreset(account.DeviceProfilePresetId);

        var currentPreset = account.DeviceProfileDynamicEnabled
                                ? savedIndependentPreset ?? sharedPreset
                                : sharedPreset;

        GeneratedUtcTicks = account.DeviceProfileDynamicEnabled
                                ? account.DeviceProfileLastGeneratedUtcTicks > 0
                                      ? account.DeviceProfileLastGeneratedUtcTicks
                                      : currentPreset.GeneratedUtcTicks
                                : sharedPreset.GeneratedUtcTicks;
        snapshotTouched = false;

        AccountDisplayName     = account.DisplayName;
        DynamicEnabled         = account.DeviceProfileDynamicEnabled;
        PeriodicRefreshEnabled = account.IsDeviceProfileRotation;
        RotationDays           = AccountManager.NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays);

        ApplyPreset(currentPreset);
    }

    public void LoadTemporary(XIVAccount targetAccount)
    {
        account                        = targetAccount;
        persistChangesToAccountManager = false;

        ApplyMode(false);
        LoadPresets();

        var sharedPreset = GetRequiredSharedPreset();
        savedIndependentPreset = accountManager.FindDeviceProfilePreset(account.DeviceProfilePresetId);

        var currentPreset = account.DeviceProfileDynamicEnabled
                                ? savedIndependentPreset ?? sharedPreset
                                : sharedPreset;

        GeneratedUtcTicks = account.DeviceProfileDynamicEnabled
                                ? account.DeviceProfileLastGeneratedUtcTicks > 0
                                      ? account.DeviceProfileLastGeneratedUtcTicks
                                      : currentPreset.GeneratedUtcTicks
                                : sharedPreset.GeneratedUtcTicks;
        snapshotTouched = false;

        AccountDisplayName     = account.DisplayName;
        DynamicEnabled         = account.DeviceProfileDynamicEnabled;
        PeriodicRefreshEnabled = account.IsDeviceProfileRotation;
        RotationDays           = AccountManager.NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays);

        ApplyPreset(currentPreset);
    }

    public void LoadShared()
    {
        account                        = null;
        persistChangesToAccountManager = true;

        ApplyMode(true);
        LoadPresets();

        var sharedPreset = GetRequiredSharedPreset();

        savedIndependentPreset = null;
        GeneratedUtcTicks      = sharedPreset.GeneratedUtcTicks;
        snapshotTouched        = false;
        AccountDisplayName     = "共享设备信息";
        DynamicEnabled         = false;
        PeriodicRefreshEnabled = false;
        RotationDays           = AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS;

        ApplyPreset(sharedPreset);
    }

    public void Save()
    {
        var snapshot = CreateValidatedSnapshot();
        if (!snapshotTouched && HasSnapshotChanged(snapshot))
            GeneratedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;

        if (IsSharedMode)
        {
            var sharedPreset = accountManager.SaveSharedDeviceProfileSelection(snapshot, GeneratedUtcTicks, PresetRemark);
            LoadPresets();
            ApplyPreset(sharedPreset);
            GeneratedUtcTicks = sharedPreset.GeneratedUtcTicks;
            snapshotTouched   = false;
            return;
        }

        var targetAccount = account ?? throw new InvalidOperationException("当前未加载账号设备信息。");
        var shouldRestoreSavedPreset = DynamicEnabled
                                       && !targetAccount.DeviceProfileDynamicEnabled
                                       && !snapshotTouched
                                       && savedIndependentPreset != null;

        if (persistChangesToAccountManager)
        {
            accountManager.UpdateDeviceProfileSettings
            (
                targetAccount,
                DynamicEnabled,
                PeriodicRefreshEnabled,
                RotationDays
            );
        }
        else
        {
            targetAccount.DeviceProfileDynamicEnabled = DynamicEnabled;
            targetAccount.IsDeviceProfileRotation     = PeriodicRefreshEnabled;
            targetAccount.DeviceProfileRotationDays   = RotationDays;
        }

        if (!DynamicEnabled)
        {
            if (snapshotTouched)
            {
                var preservedPreset = persistChangesToAccountManager
                                          ? accountManager.SaveDeviceProfileSelection(targetAccount, snapshot, GeneratedUtcTicks, PresetRemark)
                                          : accountManager.ApplyDeviceProfileSelection(targetAccount, snapshot, GeneratedUtcTicks, PresetRemark);
                savedIndependentPreset = preservedPreset;
            }

            snapshotTouched = false;
            return;
        }

        var assignedPreset = shouldRestoreSavedPreset
                                 ? SaveAccountDeviceProfileSelection
                                 (
                                     targetAccount,
                                     savedIndependentPreset!.ToSnapshot(),
                                     targetAccount.DeviceProfileLastGeneratedUtcTicks > 0
                                         ? targetAccount.DeviceProfileLastGeneratedUtcTicks
                                         : savedIndependentPreset.GeneratedUtcTicks,
                                     PresetRemark
                                 )
                                 : SaveAccountDeviceProfileSelection(targetAccount, snapshot, GeneratedUtcTicks, PresetRemark);

        savedIndependentPreset = assignedPreset;
        GeneratedUtcTicks      = targetAccount.DeviceProfileLastGeneratedUtcTicks;
        snapshotTouched        = false;

        LoadPresets();
        ApplyPreset(assignedPreset);
    }

    public void CreatePreset()
    {
        var snapshot = CreateValidatedSnapshot();
        var created  = accountManager.CreateDeviceProfilePreset(snapshot, GeneratedUtcTicks, PresetRemark);

        LoadPresets();
        ApplyPreset(created);

        if (IsSharedMode) accountManager.SaveSharedDeviceProfileSelection(created);
        else if (account != null) savedIndependentPreset = accountManager.SaveDeviceProfileSelection(account, created);

        snapshotTouched = false;
    }

    public void DeletePreset()
    {
        if (string.IsNullOrWhiteSpace(SelectedPresetId))
            throw new InvalidOperationException("请先选择要删除的预设。");

        var deletedPresetId = SelectedPresetId;
        var replacement     = accountManager.DeleteDeviceProfilePreset(deletedPresetId);

        LoadPresets();

        if (IsSharedMode) accountManager.SaveSharedDeviceProfileSelection(replacement);
        else if (account != null) savedIndependentPreset = accountManager.SaveDeviceProfileSelection(account, replacement);

        ApplyPreset(replacement);
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
        long   generatedUtcTicks,
        string presetRemark
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

        ApplySnapshot
        (
            new DeviceProfileSnapshot
            {
                DeviceId   = normalizedDeviceId,
                MacAddress = normalizedMacAddress,
                HostName   = normalizedHostName
            }
        );

        GeneratedUtcTicks = generatedUtcTicks > 0 ? generatedUtcTicks : DateTimeOffset.UtcNow.UtcTicks;
        PresetRemark      = presetRemark;
        snapshotTouched   = true;
    }

    public void RefreshAll()
    {
        ApplySnapshot(FakeMachineInfo.CreateSnapshot());
        TouchGeneratedTime();
    }

    public void UseLocalMachineInfo()
    {
        ApplySnapshot(RealMachineInfo.CreateSnapshot());
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

    private static bool TryNormalizeDeviceId(string input, out string normalizedValue, out string errorMessage)
    {
        normalizedValue = input.Trim().ToUpperInvariant();

        if (!DeviceIdPattern.IsMatch(normalizedValue))
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
        OnPropertyChanged(nameof(CanSelectPreset));
    }

    private void LoadPresets()
    {
        Presets.Clear();

        foreach (var preset in accountManager.GetDeviceProfilePresets())
            Presets.Add(preset);
    }

    private DeviceProfilePreset GetRequiredSharedPreset() =>
        accountManager.GetSharedDeviceProfilePreset();

    private DeviceProfilePreset? GetPreset(string presetId) =>
        Presets.FirstOrDefault(preset => string.Equals(preset.Id, presetId, StringComparison.Ordinal))
        ?? accountManager.FindDeviceProfilePreset(presetId);

    private void ApplyPreset(DeviceProfilePreset preset)
    {
        selectedPresetSnapshot = preset.ToSnapshot();
        PresetRemark           = preset.Remark;
        SetSelectedPresetId(preset.Id);
        ApplySnapshot(selectedPresetSnapshot);
    }

    private void ApplySelectedPreset(string presetId)
    {
        var preset = GetPreset(presetId);

        if (preset == null)
        {
            selectedPresetSnapshot = null;
            return;
        }

        selectedPresetSnapshot = preset.ToSnapshot();
        GeneratedUtcTicks      = preset.GeneratedUtcTicks;
        PresetRemark           = preset.Remark;
        snapshotTouched        = false;
        ApplySnapshot(selectedPresetSnapshot);
    }

    private void SetSelectedPresetId(string presetId)
    {
        isApplyingPresetSelection = true;
        SelectedPresetId          = presetId;
        isApplyingPresetSelection = false;
    }

    private void ApplySnapshot(DeviceProfileSnapshot snapshot)
    {
        isApplyingSnapshot = true;

        try
        {
            DeviceId   = snapshot.DeviceId;
            MacAddress = snapshot.MacAddress;
            HostName   = snapshot.HostName;
        }
        finally
        {
            isApplyingSnapshot = false;
        }

        SyncSelectedPresetWithCurrentSnapshot();
    }

    private void OnSnapshotFieldsChanged()
    {
        if (isApplyingSnapshot)
            return;

        SyncSelectedPresetWithCurrentSnapshot();
    }

    private void SyncSelectedPresetWithCurrentSnapshot()
    {
        if (!TryCreateCurrentSnapshot(out var snapshot))
        {
            selectedPresetSnapshot = null;
            if (!isApplyingPresetSelection)
                SetSelectedPresetId(string.Empty);
            return;
        }

        if (!string.IsNullOrWhiteSpace(SelectedPresetId))
        {
            var selectedPreset = GetPreset(SelectedPresetId);

            if (selectedPreset != null && selectedPreset.Matches(snapshot))
            {
                selectedPresetSnapshot = selectedPreset.ToSnapshot();
                return;
            }
        }

        var matchedPreset = Presets.FirstOrDefault(preset => preset.Matches(snapshot));

        if (matchedPreset == null)
        {
            selectedPresetSnapshot = null;
            if (!isApplyingPresetSelection)
                SetSelectedPresetId(string.Empty);
            return;
        }

        selectedPresetSnapshot = matchedPreset.ToSnapshot();
        SetSelectedPresetId(matchedPreset.Id);
    }

    private bool TryCreateCurrentSnapshot(out DeviceProfileSnapshot snapshot)
    {
        if (!TryNormalizeDeviceId(DeviceId, out var normalizedDeviceId, out _)
            || !TryNormalizeMacAddress(MacAddress, out var normalizedMacAddress, out _)
            || !TryNormalizeHostName(HostName, out var normalizedHostName, out _))
        {
            snapshot = null!;
            return false;
        }

        snapshot = new DeviceProfileSnapshot
        {
            DeviceId   = normalizedDeviceId,
            MacAddress = normalizedMacAddress,
            HostName   = normalizedHostName
        };
        return true;
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

    private DeviceProfilePreset SaveAccountDeviceProfileSelection(XIVAccount targetAccount, DeviceProfileSnapshot snapshot, long generatedUtcTicks, string? presetRemark) =>
        persistChangesToAccountManager
            ? accountManager.SaveDeviceProfileSelection(targetAccount, snapshot, generatedUtcTicks, presetRemark)
            : accountManager.ApplyDeviceProfileSelection(targetAccount, snapshot, generatedUtcTicks, presetRemark);

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

    private bool HasSnapshotChanged(DeviceProfileSnapshot snapshot) =>
        selectedPresetSnapshot == null || !selectedPresetSnapshot.Equals(snapshot);

    private void TouchGeneratedTime()
    {
        GeneratedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        snapshotTouched   = true;
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}
