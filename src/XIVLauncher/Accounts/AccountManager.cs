using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Castle.Core.Internal;
using Serilog;
using SQLite;
using XIVLauncher.Accounts.Cred;
using XIVLauncher.Accounts.Cred.CredProviders;
using XIVLauncher.Accounts.DeviceProfiles;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Settings;
using XIVLauncher.Windows;
using Lock = System.Threading.Lock;

namespace XIVLauncher.Accounts;

public class AccountManager
{
    public XIVAccount? CurrentAccount
    {
        get => Accounts.Count > 1 ? Accounts.FirstOrDefault(a => a.ID == setting.CurrentAccountId)! : Accounts.FirstOrDefault()!;
        set => setting.CurrentAccountId = value?.ID!;
    }

    public ICredProvider CredProvider { get; private set; } = null!;

    public CredType CurrentCredType { get; private set; } = DEFAULT_CRED_TYPE;

    public string CurrentAccountId => setting.CurrentAccountId;

    public bool HasCurrentAccountSelection => !string.IsNullOrWhiteSpace(setting.CurrentAccountId);

    public bool HasUnavailableSavedSecrets => unavailableSavedSecretAccountIds.Count != 0;

    public const  int      DEFAULT_DEVICE_PROFILE_ROTATION_DAYS = 7;
    private const CredType DEFAULT_CRED_TYPE                    = CredType.WindowsCredManager;

    private static readonly string DeviceProfilePresetStorePath  = Path.Combine(Paths.RoamingPath, "deviceProfilePresets.json");
    private static readonly string LegacySharedDeviceProfilePath = Path.Combine(Paths.RoamingPath, "sharedDeviceProfile.json");

    private static readonly JsonSerializerOptions DeviceProfilePresetStoreJsonOptions = new() { WriteIndented = true };

    public ObservableCollection<XIVAccount> Accounts;

    private static readonly Lock DeviceProfilePresetStoreSyncRoot = new();
    private readonly        Lock syncRoot                         = new();

    private readonly ILauncherSettingsV3 setting;

    private readonly CredData credData;

    private static   DeviceProfilePresetStoreState? deviceProfilePresetStore;
    private readonly HashSet<string>                unavailableSavedSecretAccountIds = [];

    private SQLiteConnection? db;

    public AccountManager(ILauncherSettingsV3 setting)
    {
        Load();

        this.setting = setting;

        var credPath = Path.Combine(Paths.RoamingPath, "cred.json");
        credData = new CredData("XIVLauncherCN", credPath);

        Accounts.CollectionChanged += Accounts_CollectionChanged;
    }

    public static int NormalizeDeviceProfileRotationDays(int rotationDays) =>
        rotationDays < 1 ? DEFAULT_DEVICE_PROFILE_ROTATION_DAYS : rotationDays;

    public async Task<string?> Encrypt(string text)
    {
        try
        {
            if (text is null)
                return null;

            if (CredProvider == null)
                throw new Exception("CredProvider is null");
            return await CredProvider.Encrypt(text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to encrypt text");
            CustomMessageBox.Show
            (
                ex.ToString(),
                "XIVLauncher Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }

        return null;
    }

    public async Task<string> Decrypt(string text)
    {
        try
        {
            if (text is null)
                return null;

            if (CredProvider == null)
                throw new Exception("CredProvider is null");
            return await CredProvider.Decrypt(text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to encrypt text");
            CustomMessageBox.Show
            (
                ex.ToString(),
                "XIVLauncher Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }

        return null;
    }

    public Task<CredTypeApplyResult> InitializeCredProviderAsync(CredType? requestedType) =>
        ChangeCredTypeAsync(requestedType.GetValueOrDefault(DEFAULT_CRED_TYPE), true);

    public async Task<bool> IsCredTypeSupportedAsync(CredType type) =>
        await GetCredProvider(type).IsSupported();

    public async Task<CredTypeApplyResult> ChangeCredTypeAsync(CredType requestedType, bool isStartup = false)
    {
        if (CredProvider != null && requestedType == CurrentCredType)
        {
            return new CredTypeApplyResult
            (
                true,
                requestedType,
                CurrentCredType,
                HasUnavailableSavedSecrets: HasUnavailableSavedSecrets
            );
        }

        var previousCredType = CurrentCredType;
        var oldCred          = CredProvider;
        var newCred          = GetCredProvider(requestedType);
        var isSupported      = await newCred.IsSupported();

        if (!isSupported)
        {
            if (isStartup && requestedType == CredType.WindowsHello)
                return await ApplyStartupFallbackAsync(requestedType);

            var unsupportedMessage = requestedType == CredType.WindowsHello
                                         ? "当前设备不支持 Windows Hello，请改用系统凭据管理器。"
                                         : $"当前设备不支持 {GetCredTypeDisplayName(requestedType)}。";

            Log.Warning("凭据类型 {CredType} 当前设备不可用", GetCredTypeDisplayName(requestedType));

            return new CredTypeApplyResult
            (
                false,
                requestedType,
                CurrentCredType,
                HasUnavailableSavedSecrets: HasUnavailableSavedSecrets,
                UserMessage: unsupportedMessage
            );
        }

        try
        {
            if (oldCred != null)
            {
                var testText  = EncryptionHelper.GetRandomHexString(32);
                var encrypted = await newCred.Encrypt(testText);
                var decrypted = await newCred.Decrypt(encrypted);
                if (testText != decrypted)
                    throw new Exception($"Cred type: {requestedType} test failed");
            }

            if (oldCred == null)
            {
                CurrentCredType = requestedType;
                CredProvider    = newCred;

                return new CredTypeApplyResult
                (
                    true,
                    requestedType,
                    requestedType,
                    HasUnavailableSavedSecrets: HasUnavailableSavedSecrets
                );
            }

            Log.Information
            (
                "开始切换自动登录加密方式：{OldCredType} -> {NewCredType}",
                GetCredTypeDisplayName(previousCredType),
                GetCredTypeDisplayName(requestedType)
            );

            foreach (var item in Accounts)
            {
                if (HasUnavailableSecrets(item))
                {
                    Log.Warning("账号 {AccountID} 存在当前会话不可读的旧密文，已跳过迁移", item.ID);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(item.SdoPassword))
                {
                    try
                    {
                        var password = await oldCred.Decrypt(item.SdoPassword);
                        item.SdoPassword = await newCred.Encrypt(password);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "迁移账号 {AccountID} 的登录密码失败", item.ID);
                    }
                }

                if (!string.IsNullOrWhiteSpace(item.SdoAutoLoginSessionKey))
                {
                    try
                    {
                        var sessionKey = await oldCred.Decrypt(item.SdoAutoLoginSessionKey);
                        item.SdoAutoLoginSessionKey = await newCred.Encrypt(sessionKey);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "迁移账号 {AccountID} 的自动登录密钥失败", item.ID);
                    }
                }

                if (!string.IsNullOrWhiteSpace(item.WeGameSIDSecret))
                {
                    try
                    {
                        var testSid = await oldCred.Decrypt(item.WeGameSIDSecret);
                        item.WeGameSIDSecret = await newCred.Encrypt(testSid);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "迁移账号 {AccountID} 的 WeGame SID 失败", item.ID);
                    }
                }

                if (!string.IsNullOrWhiteSpace(item.WeGameTokenSecret))
                {
                    try
                    {
                        var weGameTokenSecret = await oldCred.Decrypt(item.WeGameTokenSecret);
                        item.WeGameTokenSecret = await newCred.Encrypt(weGameTokenSecret);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "迁移账号 {AccountID} 的 WeGame Token 失败", item.ID);
                    }
                }

                if (!string.IsNullOrWhiteSpace(item.WeGameSessionID))
                {
                    try
                    {
                        var gameSessionID = await oldCred.Decrypt(item.WeGameSessionID);
                        item.WeGameSessionID = await newCred.Encrypt(gameSessionID);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "迁移账号 {AccountID} 的游戏会话 ID 失败", item.ID);
                    }
                }
            }

            CurrentCredType = requestedType;
            CredProvider    = newCred;
            Save();

            Log.Information
            (
                "自动登录加密方式切换完成：{OldCredType} -> {NewCredType}",
                GetCredTypeDisplayName(previousCredType),
                GetCredTypeDisplayName(requestedType)
            );

            return new CredTypeApplyResult
            (
                true,
                requestedType,
                requestedType,
                HasUnavailableSavedSecrets: HasUnavailableSavedSecrets
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "切换自动登录加密方式失败：{CredType}", GetCredTypeDisplayName(requestedType));

            return new CredTypeApplyResult
            (
                false,
                requestedType,
                CurrentCredType,
                HasUnavailableSavedSecrets: HasUnavailableSavedSecrets,
                UserMessage: $"切换到 {GetCredTypeDisplayName(requestedType)} 失败，请稍后重试。"
            );
        }
    }

    public bool HasUnavailableSecrets(XIVAccount? account) =>
        account != null && unavailableSavedSecretAccountIds.Contains(account.ID);

    public void AddAccount(XIVAccount account)
    {
        if (account.UserName.IsNullOrEmpty() || account.ID.IsNullOrEmpty())
            throw new Exception($"UserName:{account.UserName} ID:{account.ID} 不能为空");

        var existingAccount = Accounts.FirstOrDefault(a => a.Equals(account));

        Log.Verbose($"existingAccount: {existingAccount?.ID}");

        if (existingAccount != null)
        {
            Log.Verbose("Updating account...");
            existingAccount.ID                                 = account.ID;
            existingAccount.SdoPassword                        = account.SdoPassword;
            existingAccount.AutoLogin                          = account.AutoLogin;
            existingAccount.SdoAutoLoginSessionKey             = account.SdoAutoLoginSessionKey;
            existingAccount.WeGameTokenSecret                  = account.WeGameTokenSecret;
            existingAccount.WeGameSIDSecret                    = account.WeGameSIDSecret;
            existingAccount.AreaName                           = account.AreaName;
            existingAccount.WeGameSessionID                    = account.WeGameSessionID;
            existingAccount.DeviceProfileDeviceId              = account.DeviceProfileDeviceId;
            existingAccount.DeviceProfileMacAddress            = account.DeviceProfileMacAddress;
            existingAccount.DeviceProfileHostName              = account.DeviceProfileHostName;
            existingAccount.DeviceProfilePresetId              = account.DeviceProfilePresetId;
            existingAccount.DeviceProfileDynamicEnabled        = account.DeviceProfileDynamicEnabled;
            existingAccount.IsDeviceProfileRotation            = account.IsDeviceProfileRotation;
            existingAccount.DeviceProfileRotationDays          = NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays);
            existingAccount.DeviceProfileLastGeneratedUtcTicks = account.DeviceProfileLastGeneratedUtcTicks;
        }
        else
            Accounts.Add(account);

        ClearUnavailableSecrets(account.ID);
    }

    public XIVAccount? FindAccount(string? userName, XIVAccountType accountType)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return null;

        return Accounts.FirstOrDefault
        (account => account.AccountType == accountType && string.Equals(account.UserName, userName, StringComparison.Ordinal)
        );
    }

    public IReadOnlyList<DeviceProfilePreset> GetDeviceProfilePresets()
    {
        lock (DeviceProfilePresetStoreSyncRoot)
        {
            var state = GetDeviceProfilePresetStoreState();
            return state.Presets
                        .OrderBy(preset => preset.DisplayName, StringComparer.Ordinal)
                        .ToArray();
        }
    }

    public DeviceProfilePreset? FindDeviceProfilePreset(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
            return null;

        lock (DeviceProfilePresetStoreSyncRoot)
            return FindPresetById(GetDeviceProfilePresetStoreState(), presetId);
    }

    public string GetSharedDeviceProfilePresetId() =>
        GetSharedDeviceProfilePreset().Id;

    public DeviceProfilePreset GetSharedDeviceProfilePreset()
    {
        lock (DeviceProfilePresetStoreSyncRoot)
            return GetSharedDeviceProfilePreset(GetDeviceProfilePresetStoreState());
    }

    public ResolvedDeviceProfile ResolveDeviceProfile(string? userName, XIVAccountType accountType)
    {
        var sharedPreset = GetSharedDeviceProfilePreset();
        var account      = FindAccount(userName, accountType);

        return ResolveDeviceProfile(sharedPreset, account, false);
    }

    public ResolvedDeviceProfile ResolveDeviceProfile(XIVAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var sharedPreset     = GetSharedDeviceProfilePreset();
        var trackedAccount   = GetTrackedAccount(account);
        var isTrackedAccount = Accounts.Any(existing => existing.ID == trackedAccount.ID);

        return ResolveDeviceProfile(sharedPreset, trackedAccount, isTrackedAccount);
    }

    private ResolvedDeviceProfile ResolveDeviceProfile(DeviceProfilePreset sharedPreset, XIVAccount? account, bool saveChanges)
    {
        if (account == null)
        {
            return new ResolvedDeviceProfile
            (
                sharedPreset.ToSnapshot(),
                null,
                false,
                true,
                DEFAULT_DEVICE_PROFILE_ROTATION_DAYS,
                sharedPreset.GeneratedUtcTicks
            );
        }

        var isChanged = NormalizeDeviceProfileSettings(account) || MigrateLegacyDeviceProfile(account);

        if (!account.DeviceProfileDynamicEnabled)
        {
            if (isChanged && saveChanges)
                Save(account);

            return new ResolvedDeviceProfile
            (
                sharedPreset.ToSnapshot(),
                null,
                false,
                account.IsDeviceProfileRotation,
                NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays),
                sharedPreset.GeneratedUtcTicks
            );
        }

        var nowUtc        = DateTimeOffset.UtcNow;
        var accountPreset = EnsureAccountDeviceProfilePreset(account, sharedPreset, nowUtc, ref isChanged);

        if (ShouldRotateDeviceProfile(account, nowUtc))
        {
            accountPreset = AssignPresetToAccount(account, FakeMachineInfo.CreateSnapshot(), nowUtc.UtcTicks);
            isChanged     = true;
        }

        if (isChanged && saveChanges)
            Save(account);

        return new ResolvedDeviceProfile
        (
            accountPreset.ToSnapshot(),
            account.DeviceProfilePresetId,
            account.DeviceProfileDynamicEnabled,
            account.IsDeviceProfileRotation,
            NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays),
            account.DeviceProfileLastGeneratedUtcTicks
        );
    }

    public long GetSharedDeviceProfileGeneratedUtcTicks() =>
        GetSharedDeviceProfilePreset().GeneratedUtcTicks;

    public DeviceProfileSnapshot GetSharedDeviceProfileSnapshot() =>
        GetSharedDeviceProfilePreset().ToSnapshot();

    public void UpdateDeviceProfileSettings(XIVAccount account, bool dynamicEnabled, bool isDeviceProfileRotation, int rotationDays)
    {
        var trackedAccount = GetTrackedAccount(account);

        trackedAccount.DeviceProfileDynamicEnabled = dynamicEnabled;
        trackedAccount.IsDeviceProfileRotation     = isDeviceProfileRotation;
        trackedAccount.DeviceProfileRotationDays   = NormalizeDeviceProfileRotationDays(rotationDays);

        Save(trackedAccount);
    }

    public DeviceProfilePreset SaveDeviceProfileSelection(XIVAccount account, DeviceProfileSnapshot snapshot, long generatedUtcTicks, string? remark)
    {
        var trackedAccount = GetTrackedAccount(account);
        var preset         = ApplyDeviceProfileSelection(trackedAccount, snapshot, generatedUtcTicks, remark);
        Save(trackedAccount);
        return preset;
    }

    public DeviceProfilePreset ApplyDeviceProfileSelection(XIVAccount account, DeviceProfileSnapshot snapshot, long generatedUtcTicks, string? remark) =>
        AssignPresetToAccount(account, snapshot, NormalizeGeneratedUtcTicks(generatedUtcTicks), remark);

    public DeviceProfilePreset SaveSharedDeviceProfileSelection(DeviceProfileSnapshot snapshot, long generatedUtcTicks, string? remark)
    {
        lock (DeviceProfilePresetStoreSyncRoot)
        {
            var state  = GetDeviceProfilePresetStoreState();
            var preset = FindOrCreatePreset(state, snapshot, NormalizeGeneratedUtcTicks(generatedUtcTicks), remark);

            if (!string.Equals(state.SharedPresetId, preset.Id, StringComparison.Ordinal))
            {
                var updatedState = new DeviceProfilePresetStoreState
                {
                    Version        = state.Version,
                    SharedPresetId = preset.Id,
                    Presets        = state.Presets
                };
                PersistDeviceProfilePresetStoreState(updatedState);
                return preset;
            }

            PersistDeviceProfilePresetStoreState(state);
            return preset;
        }
    }

    public void ApplyResolvedDeviceProfile(XIVAccount account, ResolvedDeviceProfile resolvedDeviceProfile)
    {
        account.DeviceProfileDynamicEnabled = resolvedDeviceProfile.DynamicEnabled;
        account.IsDeviceProfileRotation     = resolvedDeviceProfile.IsRotationEnabled;
        account.DeviceProfileRotationDays   = NormalizeDeviceProfileRotationDays(resolvedDeviceProfile.RotationDays);

        if (!resolvedDeviceProfile.DynamicEnabled)
            return;

        ClearLegacyDeviceProfileSnapshot(account);
        account.DeviceProfilePresetId              = resolvedDeviceProfile.PresetId ?? string.Empty;
        account.DeviceProfileLastGeneratedUtcTicks = resolvedDeviceProfile.LastGeneratedUtcTicks;
    }

    public void RemoveAccount(XIVAccount account)
    {
        account.SdoPassword       = string.Empty;
        account.WeGameTokenSecret = null;
        ClearUnavailableSecrets(account.ID);
        Accounts.Remove(account);

        if (string.Equals(setting.CurrentAccountId, account.ID, StringComparison.Ordinal))
            setting.CurrentAccountId = string.Empty;

        AccountSwitcherEntry.RemoveCustomProfileImage(account);

        var profileIconPath = Path.Combine
        (
            Paths.RoamingPath,
            "profileIcons",
            $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.ID)))}.ico"
        );
        if (File.Exists(profileIconPath))
            File.Delete(profileIconPath);

        if (!string.IsNullOrWhiteSpace(account.DeviceProfilePresetId))
        {
            lock (DeviceProfilePresetStoreSyncRoot)
            {
                var state = GetDeviceProfilePresetStoreState();

                if (!string.Equals(state.SharedPresetId, account.DeviceProfilePresetId, StringComparison.Ordinal)
                    && Accounts.All(existing => !string.Equals(existing.DeviceProfilePresetId, account.DeviceProfilePresetId, StringComparison.Ordinal)))
                {
                    state.Presets.RemoveAll(preset => string.Equals(preset.Id, account.DeviceProfilePresetId, StringComparison.Ordinal));
                    PersistDeviceProfilePresetStoreState(state);
                }
            }
        }

        lock (syncRoot)
        {
            db.RunInTransaction
            (() =>
                {
                    var record = db.Table<XIVAccount>().FirstOrDefault(a => a.ID == account.ID);
                    if (record != null)
                        db.Delete(record);
                }
            );
        }
    }

    public void ClearCurrentAccount() =>
        setting.CurrentAccountId = string.Empty;

    public static string GetCredTypeDisplayName(CredType type) =>
        type switch
        {
            CredType.NoEncryption       => "无加密",
            CredType.WindowsCredManager => "系统凭据管理器",
            CredType.WindowsHello       => "Windows Hello",
            _                           => type.ToString()
        };

    private async Task<CredTypeApplyResult> ApplyStartupFallbackAsync(CredType requestedType)
    {
        const CredType fallbackType = DEFAULT_CRED_TYPE;

        var fallbackCred      = GetCredProvider(fallbackType);
        var fallbackSupported = await fallbackCred.IsSupported();
        if (!fallbackSupported)
            throw new InvalidOperationException($"默认凭据类型 {fallbackType} 当前不可用");

        CredProvider    = fallbackCred;
        CurrentCredType = fallbackType;
        MarkUnavailableSecretsFromExistingAccounts();

        const string USER_MESSAGE = "当前设备不支持 Windows Hello，已自动切换为系统凭据管理器，并关闭自动登录。此前保存的密码或自动登录凭据需要重新输入后再保存。";

        Log.Warning
        (
            "当前设备不支持 {RequestedCredType}，已自动切换为 {FallbackCredType}，并关闭自动登录",
            GetCredTypeDisplayName(requestedType),
            GetCredTypeDisplayName(fallbackType)
        );

        return new CredTypeApplyResult
        (
            true,
            requestedType,
            fallbackType,
            true,
            true,
            HasUnavailableSavedSecrets,
            USER_MESSAGE
        );
    }

    private void MarkUnavailableSecretsFromExistingAccounts()
    {
        unavailableSavedSecretAccountIds.Clear();

        foreach (var account in Accounts.Where(HasStoredSecrets))
            unavailableSavedSecretAccountIds.Add(account.ID);
    }

    private void ClearUnavailableSecrets(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return;

        unavailableSavedSecretAccountIds.Remove(accountId);
    }

    private static bool HasStoredSecrets(XIVAccount account) =>
        !string.IsNullOrWhiteSpace(account.SdoPassword)
        || !string.IsNullOrWhiteSpace(account.SdoAutoLoginSessionKey)
        || !string.IsNullOrWhiteSpace(account.WeGameTokenSecret)
        || !string.IsNullOrWhiteSpace(account.WeGameSIDSecret)
        || !string.IsNullOrWhiteSpace(account.WeGameSessionID);

    private ICredProvider GetCredProvider(CredType type) =>
        type switch
        {
            CredType.WindowsCredManager => new CredentialManager(credData),
            CredType.WindowsHello       => new WindowsHello(credData),
            CredType.NoEncryption       => new NoCred(credData),
            _                           => throw new ArgumentOutOfRangeException(nameof(type), type, "未知的凭据类型")
        };

    private void Accounts_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Save();

    #region SaveLoad

    private static readonly string DatabasePath = Path.Combine(Paths.RoamingPath, "accounts.db");

    public void Save(XIVAccount account)
    {
        lock (syncRoot)
        {
            db.RunInTransaction
            (() =>
                {
                    var record = db.Table<XIVAccount>().FirstOrDefault(a => a.ID == account.ID);

                    if (record == null)
                        db.Insert(account);
                    else
                    {
                        record = account;
                        db.Update(record);
                    }
                }
            );
        }
    }

    public void Save()
    {
        foreach (var item in Accounts)
            Save(item);
    }

    public void SetupDb()
    {
        db = new SQLiteConnection
        (
            DatabasePath,
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex
        );
        db.CreateTable<XIVAccount>();
        MigrateAccountsTable();
    }

    public void Load()
    {
        try
        {
            SetupDb();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load VFS database, starting fresh");

            if (File.Exists(DatabasePath))
                File.Delete(DatabasePath);

            SetupDb();
        }

        // If the file is corrupted, this will be null anyway
        Accounts ??= new ObservableCollection<XIVAccount>(db.Table<XIVAccount>());

        foreach (var account in Accounts.ToArray())
        {
            if (account.UserName.IsNullOrEmpty() || account.ID.IsNullOrEmpty())
                Accounts.Remove(account);
        }

        MigrateLegacyDeviceProfiles();
    }

    private XIVAccount GetTrackedAccount(XIVAccount account) =>
        Accounts.FirstOrDefault(existing => existing.ID == account.ID) ?? account;

    private static bool HasDeviceProfile(XIVAccount account) =>
        !string.IsNullOrWhiteSpace(account.DeviceProfileDeviceId)
        && !string.IsNullOrWhiteSpace(account.DeviceProfileMacAddress)
        && !string.IsNullOrWhiteSpace(account.DeviceProfileHostName);

    private static DeviceProfileSnapshot CreateDeviceProfileSnapshot(XIVAccount account) =>
        new()
        {
            DeviceId   = account.DeviceProfileDeviceId,
            MacAddress = account.DeviceProfileMacAddress,
            HostName   = account.DeviceProfileHostName
        };

    private static void ClearLegacyDeviceProfileSnapshot(XIVAccount account)
    {
        account.DeviceProfileDeviceId   = string.Empty;
        account.DeviceProfileMacAddress = string.Empty;
        account.DeviceProfileHostName   = string.Empty;
    }

    private static bool ShouldRotateDeviceProfile(XIVAccount account, DateTimeOffset nowUtc)
    {
        if (!account.DeviceProfileDynamicEnabled || !account.IsDeviceProfileRotation)
            return false;

        if (account.DeviceProfileLastGeneratedUtcTicks <= 0)
            return true;

        var lastGeneratedUtc = new DateTimeOffset(account.DeviceProfileLastGeneratedUtcTicks, TimeSpan.Zero);
        var interval         = TimeSpan.FromDays(NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays));
        return nowUtc - lastGeneratedUtc >= interval;
    }

    private static bool NormalizeDeviceProfileSettings(XIVAccount account)
    {
        var normalizedRotationDays = NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays);
        var isChanged              = false;

        if (account.DeviceProfileRotationDays != normalizedRotationDays)
        {
            account.DeviceProfileRotationDays = normalizedRotationDays;
            isChanged                         = true;
        }

        return isChanged;
    }

    private void MigrateLegacyDeviceProfiles()
    {
        lock (DeviceProfilePresetStoreSyncRoot)
            _ = GetDeviceProfilePresetStoreState();

        foreach (var account in Accounts)
        {
            if (!MigrateLegacyDeviceProfile(account))
                continue;

            Save(account);
        }
    }

    private bool MigrateLegacyDeviceProfile(XIVAccount account)
    {
        var isChanged = false;

        if (HasDeviceProfile(account))
        {
            var preset = FindOrCreatePreset(CreateDeviceProfileSnapshot(account), account.DeviceProfileLastGeneratedUtcTicks);

            if (!string.Equals(account.DeviceProfilePresetId, preset.Id, StringComparison.Ordinal))
            {
                account.DeviceProfilePresetId = preset.Id;
                isChanged                     = true;
            }

            if (account.DeviceProfileLastGeneratedUtcTicks <= 0)
            {
                account.DeviceProfileLastGeneratedUtcTicks = NormalizeGeneratedUtcTicks(preset.GeneratedUtcTicks);
                isChanged                                  = true;
            }

            ClearLegacyDeviceProfileSnapshot(account);
            isChanged = true;
        }
        else if (!string.IsNullOrWhiteSpace(account.DeviceProfilePresetId)
                 && FindDeviceProfilePreset(account.DeviceProfilePresetId) == null)
        {
            account.DeviceProfilePresetId = string.Empty;
            isChanged                     = true;
        }

        return isChanged;
    }

    private void MigrateAccountsTable()
    {
        var columns = db!.Query<SQLiteTableColumn>("PRAGMA table_info(\"XIVAccount\")")
                         .Select(column => column.name)
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

        EnsureColumn(columns, "DeviceProfileDeviceId",       "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(columns, "DeviceProfileMacAddress",     "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(columns, "DeviceProfileHostName",       "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(columns, "DeviceProfilePresetId",       "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(columns, "DeviceProfileDynamicEnabled", "INTEGER NOT NULL DEFAULT 0");
        var addedRotationColumn = EnsureColumn(columns, "IsDeviceProfileRotation", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(columns, "DeviceProfileRotationDays",          $"INTEGER NOT NULL DEFAULT {DEFAULT_DEVICE_PROFILE_ROTATION_DAYS}");
        EnsureColumn(columns, "DeviceProfileLastGeneratedUtcTicks", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(columns, "WeGameTokenSecret",                  "TEXT");

        if (addedRotationColumn && columns.Contains("DeviceProfileRotationMode"))
            db!.Execute("UPDATE \"XIVAccount\" SET \"IsDeviceProfileRotation\" = CASE WHEN \"DeviceProfileRotationMode\" = 0 THEN 1 ELSE 0 END");
    }

    private bool EnsureColumn(ISet<string> columns, string columnName, string definition)
    {
        if (columns.Contains(columnName))
            return false;

        db!.Execute($"ALTER TABLE \"XIVAccount\" ADD COLUMN \"{columnName}\" {definition}");
        columns.Add(columnName);
        return true;
    }

    private static bool HasDeviceProfile(DeviceProfileSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.DeviceId)
        && !string.IsNullOrWhiteSpace(snapshot.MacAddress)
        && !string.IsNullOrWhiteSpace(snapshot.HostName);

    private static DeviceProfilePresetStoreState GetDeviceProfilePresetStoreState()
    {
        deviceProfilePresetStore ??= LoadOrCreateDeviceProfilePresetStoreState();
        return deviceProfilePresetStore;
    }

    private static DeviceProfilePresetStoreState LoadOrCreateDeviceProfilePresetStoreState()
    {
        try
        {
            if (File.Exists(DeviceProfilePresetStorePath))
            {
                var json       = File.ReadAllText(DeviceProfilePresetStorePath, Encoding.UTF8);
                var stored     = JsonSerializer.Deserialize<DeviceProfilePresetStoreState>(json, DeviceProfilePresetStoreJsonOptions);
                var normalized = NormalizeDeviceProfilePresetStoreState(stored);

                if (normalized != null)
                {
                    PersistDeviceProfilePresetStoreState(normalized);
                    return normalized;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取设备信息预设失败，将重新生成。");
        }

        var legacyPreset = LoadLegacySharedDeviceProfilePreset()
                           ?? CreatePreset(RealMachineInfo.CreateSnapshot(), DateTimeOffset.UtcNow.UtcTicks);

        var state = new DeviceProfilePresetStoreState
        {
            SharedPresetId = legacyPreset.Id,
            Presets        = [legacyPreset]
        };

        PersistDeviceProfilePresetStoreState(state);
        TryDeleteLegacySharedDeviceProfileFile();
        return state;
    }

    private static DeviceProfilePresetStoreState? NormalizeDeviceProfilePresetStoreState(DeviceProfilePresetStoreState? state)
    {
        if (state == null)
            return null;

        var presets        = new List<DeviceProfilePreset>(state.Presets.Count);
        var sharedPresetId = string.Empty;

        foreach (var preset in state.Presets)
        {
            var snapshot = preset.ToSnapshot();
            if (!HasDeviceProfile(snapshot))
                continue;

            var normalizedPreset = new DeviceProfilePreset
            {
                Id                = string.IsNullOrWhiteSpace(preset.Id) ? Guid.NewGuid().ToString("N") : preset.Id,
                Remark            = NormalizePresetRemark(preset.Remark),
                DeviceId          = snapshot.DeviceId,
                MacAddress        = snapshot.MacAddress,
                HostName          = snapshot.HostName,
                GeneratedUtcTicks = NormalizeGeneratedUtcTicks(preset.GeneratedUtcTicks)
            };

            var existingIndex = presets.FindIndex(existing => existing.Matches(snapshot));

            if (existingIndex >= 0)
            {
                var existing                = presets[existingIndex];
                var mergedGeneratedUtcTicks = Math.Max(existing.GeneratedUtcTicks, normalizedPreset.GeneratedUtcTicks);
                var mergedRemark            = string.IsNullOrWhiteSpace(existing.Remark) ? normalizedPreset.Remark : existing.Remark;
                if (mergedGeneratedUtcTicks != existing.GeneratedUtcTicks
                    || !string.Equals(mergedRemark, existing.Remark, StringComparison.Ordinal))
                    presets[existingIndex] = existing.WithGeneratedUtcTicks(mergedGeneratedUtcTicks, mergedRemark);

                if (string.Equals(state.SharedPresetId, preset.Id, StringComparison.Ordinal))
                    sharedPresetId = presets[existingIndex].Id;

                continue;
            }

            presets.Add(normalizedPreset);

            if (string.Equals(state.SharedPresetId, preset.Id, StringComparison.Ordinal))
                sharedPresetId = normalizedPreset.Id;
        }

        if (presets.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(sharedPresetId))
            sharedPresetId = presets[0].Id;

        return new DeviceProfilePresetStoreState
        {
            Version        = 1,
            SharedPresetId = sharedPresetId,
            Presets        = presets
        };
    }

    private static DeviceProfilePreset? LoadLegacySharedDeviceProfilePreset()
    {
        try
        {
            if (!File.Exists(LegacySharedDeviceProfilePath))
                return null;

            var json   = File.ReadAllText(LegacySharedDeviceProfilePath, Encoding.UTF8);
            var legacy = JsonSerializer.Deserialize<DeviceProfilePreset>(json, DeviceProfilePresetStoreJsonOptions);
            if (legacy == null || !HasDeviceProfile(legacy.ToSnapshot()))
                return null;

            return new DeviceProfilePreset
            {
                Id                = Guid.NewGuid().ToString("N"),
                Remark            = NormalizePresetRemark(legacy.Remark),
                DeviceId          = legacy.DeviceId,
                MacAddress        = legacy.MacAddress,
                HostName          = legacy.HostName,
                GeneratedUtcTicks = NormalizeGeneratedUtcTicks(legacy.GeneratedUtcTicks)
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取旧版共享设备信息失败，将重新生成。");
            return null;
        }
    }

    private static void TryDeleteLegacySharedDeviceProfileFile()
    {
        try
        {
            if (File.Exists(LegacySharedDeviceProfilePath))
                File.Delete(LegacySharedDeviceProfilePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "删除旧版共享设备信息文件失败。");
        }
    }

    private static void PersistDeviceProfilePresetStoreState(DeviceProfilePresetStoreState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DeviceProfilePresetStorePath)!);
            var json = JsonSerializer.Serialize(state, DeviceProfilePresetStoreJsonOptions);
            File.WriteAllText(DeviceProfilePresetStorePath, json, new UTF8Encoding(false));
            deviceProfilePresetStore = state;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存设备信息预设失败。");
        }
    }

    private static DeviceProfilePreset GetSharedDeviceProfilePreset(DeviceProfilePresetStoreState state)
    {
        var preset = FindPresetById(state, state.SharedPresetId);
        if (preset != null)
            return preset;

        if (state.Presets.Count != 0)
        {
            var normalizedState = new DeviceProfilePresetStoreState
            {
                Version        = state.Version,
                SharedPresetId = state.Presets[0].Id,
                Presets        = state.Presets
            };

            PersistDeviceProfilePresetStoreState(normalizedState);
            return normalizedState.Presets[0];
        }

        var created = CreatePreset(RealMachineInfo.CreateSnapshot(), DateTimeOffset.UtcNow.UtcTicks);
        var fallbackState = new DeviceProfilePresetStoreState
        {
            SharedPresetId = created.Id,
            Presets        = [created]
        };

        PersistDeviceProfilePresetStoreState(fallbackState);
        return created;
    }

    private static DeviceProfilePreset? FindPresetById(DeviceProfilePresetStoreState state, string? presetId) =>
        string.IsNullOrWhiteSpace(presetId)
            ? null
            : state.Presets.FirstOrDefault(preset => string.Equals(preset.Id, presetId, StringComparison.Ordinal));

    private DeviceProfilePreset FindOrCreatePreset(DeviceProfileSnapshot snapshot, long generatedUtcTicks, string? remark = null)
    {
        lock (DeviceProfilePresetStoreSyncRoot)
        {
            var state  = GetDeviceProfilePresetStoreState();
            var preset = FindOrCreatePreset(state, snapshot, NormalizeGeneratedUtcTicks(generatedUtcTicks), remark);
            PersistDeviceProfilePresetStoreState(state);
            return preset;
        }
    }

    private static DeviceProfilePreset FindOrCreatePreset(DeviceProfilePresetStoreState state, DeviceProfileSnapshot snapshot, long generatedUtcTicks, string? remark = null)
    {
        var existingIndex = state.Presets.FindIndex(preset => preset.Matches(snapshot));

        if (existingIndex >= 0)
        {
            var existing              = state.Presets[existingIndex];
            var normalizedRemark      = remark == null ? existing.Remark : NormalizePresetRemark(remark);
            var updatedGeneratedTicks = Math.Max(existing.GeneratedUtcTicks, generatedUtcTicks);

            if (existing.GeneratedUtcTicks == updatedGeneratedTicks
                && string.Equals(existing.Remark, normalizedRemark, StringComparison.Ordinal))
                return existing;

            var updated = existing.WithGeneratedUtcTicks(updatedGeneratedTicks, normalizedRemark);
            state.Presets[existingIndex] = updated;
            return updated;
        }

        var created = CreatePreset(snapshot, generatedUtcTicks, remark);
        state.Presets.Add(created);
        return created;
    }

    private DeviceProfilePreset EnsureAccountDeviceProfilePreset(XIVAccount account, DeviceProfilePreset sharedPreset, DateTimeOffset nowUtc, ref bool isChanged)
    {
        var preset = FindDeviceProfilePreset(account.DeviceProfilePresetId);
        if (preset != null)
            return preset;

        if (HasDeviceProfile(account))
        {
            preset = AssignPresetToAccount
            (
                account,
                CreateDeviceProfileSnapshot(account),
                account.DeviceProfileLastGeneratedUtcTicks > 0 ? account.DeviceProfileLastGeneratedUtcTicks : nowUtc.UtcTicks
            );
            isChanged = true;
            return preset;
        }

        preset    = AssignPresetToAccount(account, sharedPreset.ToSnapshot(), nowUtc.UtcTicks);
        isChanged = true;
        return preset;
    }

    private DeviceProfilePreset AssignPresetToAccount(XIVAccount account, DeviceProfileSnapshot snapshot, long generatedUtcTicks, string? remark = null)
    {
        var normalizedTicks = NormalizeGeneratedUtcTicks(generatedUtcTicks);
        var preset          = FindOrCreatePreset(snapshot, normalizedTicks, remark);

        account.DeviceProfilePresetId              = preset.Id;
        account.DeviceProfileLastGeneratedUtcTicks = normalizedTicks;
        ClearLegacyDeviceProfileSnapshot(account);
        return preset;
    }

    private static DeviceProfilePreset CreatePreset(DeviceProfileSnapshot snapshot, long generatedUtcTicks, string? remark = null) =>
        new()
        {
            Id                = Guid.NewGuid().ToString("N"),
            Remark            = NormalizePresetRemark(remark),
            DeviceId          = snapshot.DeviceId,
            MacAddress        = snapshot.MacAddress,
            HostName          = snapshot.HostName,
            GeneratedUtcTicks = NormalizeGeneratedUtcTicks(generatedUtcTicks)
        };

    private static long NormalizeGeneratedUtcTicks(long generatedUtcTicks) =>
        generatedUtcTicks > 0
            ? generatedUtcTicks
            : DateTimeOffset.UtcNow.UtcTicks;

    private static string NormalizePresetRemark(string? remark) =>
        remark?.Trim() ?? string.Empty;

    #endregion
}
