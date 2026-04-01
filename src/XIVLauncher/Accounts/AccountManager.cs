using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Castle.Core.Internal;
using Serilog;
using SQLite;
using XIVLauncher.Accounts.Cred;
using XIVLauncher.Accounts.Cred.CredProviders;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Settings;
using XIVLauncher.Windows;
using Lock = System.Threading.Lock;

namespace XIVLauncher.Accounts;

public class AccountManager
{
    public const int DEFAULT_DEVICE_PROFILE_ROTATION_DAYS = 7;
    private const CredType DEFAULT_CRED_TYPE = CredType.WindowsCredManager;

    public ObservableCollection<XIVAccount> Accounts;

    public XIVAccount CurrentAccount
    {
        get => Accounts.Count > 1 ? Accounts.FirstOrDefault(a => a.Id == _setting.CurrentAccountId) : Accounts.FirstOrDefault();
        set => _setting.CurrentAccountId = value.Id;
    }

    public ICredProvider CredProvider { get; private set; } = null!;

    public CredType CurrentCredType { get; private set; } = DEFAULT_CRED_TYPE;

    public bool HasUnavailableSavedSecrets => unavailableSavedSecretAccountIds.Count != 0;

    private static readonly string                SharedDeviceProfilePath        = Path.Combine(Paths.RoamingPath, "sharedDeviceProfile.json");
    private static readonly JsonSerializerOptions SharedDeviceProfileJsonOptions = new() { WriteIndented = true };
    private static readonly Lock                  SharedDeviceProfileSyncRoot    = new();
    private readonly        object                syncRoot                       = new();

    private readonly ILauncherSettingsV3 _setting;

    private readonly CredData CredData;

    private static StoredDeviceProfileSnapshot? sharedDeviceProfile;
    private readonly HashSet<string> unavailableSavedSecretAccountIds = [];

    private SQLiteConnection? db;

    public AccountManager(ILauncherSettingsV3 setting)
    {
        Load();

        _setting = setting;

        var credPath = Path.Combine(Paths.RoamingPath, "cred.json");
        CredData = new CredData("XIVLauncherCN", credPath);

        Accounts.CollectionChanged += Accounts_CollectionChanged;
    }

    public static int NormalizeDeviceProfileRotationDays(int rotationDays) =>
        rotationDays < 1 ? DEFAULT_DEVICE_PROFILE_ROTATION_DAYS : rotationDays;

    public async Task<string> Encrypt(string text)
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
                    Log.Warning("账号 {AccountId} 存在当前会话不可读的旧密文，已跳过迁移", item.Id);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(item.Password))
                {
                    try
                    {
                        var password = await oldCred.Decrypt(item.Password);
                        item.Password = await newCred.Encrypt(password);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "迁移账号 {AccountId} 的登录密码失败", item.Id);
                    }
                }

                if (!string.IsNullOrWhiteSpace(item.AutoLoginSessionKey))
                {
                    try
                    {
                        var sessionKey = await oldCred.Decrypt(item.AutoLoginSessionKey);
                        item.AutoLoginSessionKey = await newCred.Encrypt(sessionKey);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "迁移账号 {AccountId} 的自动登录密钥失败", item.Id);
                    }
                }

                if (!string.IsNullOrWhiteSpace(item.TestSID))
                {
                    try
                    {
                        var testSid = await oldCred.Decrypt(item.TestSID);
                        item.TestSID = await newCred.Encrypt(testSid);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "迁移账号 {AccountId} 的 WeGame SID 失败", item.Id);
                    }
                }

                if (!string.IsNullOrWhiteSpace(item.NSessionId))
                {
                    try
                    {
                        var nSessionId = await oldCred.Decrypt(item.NSessionId);
                        item.NSessionId = await newCred.Encrypt(nSessionId);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "迁移账号 {AccountId} 的 NSessionId 失败", item.Id);
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
        account != null && unavailableSavedSecretAccountIds.Contains(account.Id);

    public void AddAccount(XIVAccount account)
    {
        if (account.UserName.IsNullOrEmpty() || account.Id.IsNullOrEmpty())
            throw new Exception($"UserName:{account.UserName} Id:{account.Id} 不能为空");

        var existingAccount = Accounts.FirstOrDefault(a => a.Equals(account));

        Log.Verbose($"existingAccount: {existingAccount?.Id}");

        if (existingAccount != null)
        {
            Log.Verbose("Updating account...");
            existingAccount.Id                                 = account.Id;
            existingAccount.Password                           = account.Password;
            existingAccount.AutoLogin                          = account.AutoLogin;
            existingAccount.AutoLoginSessionKey                = account.AutoLoginSessionKey;
            existingAccount.TestSID                            = account.TestSID;
            existingAccount.AreaName                           = account.AreaName;
            existingAccount.NSessionId                         = account.NSessionId;
            existingAccount.DeviceProfileDeviceId              = account.DeviceProfileDeviceId;
            existingAccount.DeviceProfileMacAddress            = account.DeviceProfileMacAddress;
            existingAccount.DeviceProfileHostName              = account.DeviceProfileHostName;
            existingAccount.DeviceProfileDynamicEnabled        = account.DeviceProfileDynamicEnabled;
            existingAccount.IsDeviceProfileRotation            = account.IsDeviceProfileRotation;
            existingAccount.DeviceProfileRotationDays          = NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays);
            existingAccount.DeviceProfileLastGeneratedUtcTicks = account.DeviceProfileLastGeneratedUtcTicks;
        }
        else
            Accounts.Add(account);

        ClearUnavailableSecrets(account.Id);
    }

    public XIVAccount? FindAccount(string? userName, XIVAccountType accountType)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return null;

        return Accounts.FirstOrDefault
        (account => account.AccountType == accountType && string.Equals(account.UserName, userName, StringComparison.Ordinal)
        );
    }

    public ResolvedDeviceProfile ResolveDeviceProfile(string? userName, XIVAccountType accountType)
    {
        var account = FindAccount(userName, accountType);

        if (account == null)
        {
            var sharedProfile = GetSharedDeviceProfileState();
            return new ResolvedDeviceProfile
            (
                sharedProfile.ToSnapshot(),
                false,
                true,
                DEFAULT_DEVICE_PROFILE_ROTATION_DAYS,
                sharedProfile.GeneratedUtcTicks
            );
        }

        if (!account.DeviceProfileDynamicEnabled)
        {
            var sharedProfile = GetSharedDeviceProfileState();
            return new ResolvedDeviceProfile
            (
                sharedProfile.ToSnapshot(),
                false,
                account.IsDeviceProfileRotation,
                NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays),
                sharedProfile.GeneratedUtcTicks
            );
        }

        var snapshot = GetOrCreateDeviceProfileSnapshot(account);
        return new ResolvedDeviceProfile
        (
            snapshot,
            account.DeviceProfileDynamicEnabled,
            account.IsDeviceProfileRotation,
            NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays),
            account.DeviceProfileLastGeneratedUtcTicks
        );
    }

    public DeviceProfileSnapshot GetOrCreateDeviceProfileSnapshot(XIVAccount account)
    {
        var trackedAccount = GetTrackedAccount(account);
        var nowUtc         = DateTimeOffset.UtcNow;
        var isChanged      = NormalizeDeviceProfileSettings(trackedAccount);

        if (!HasDeviceProfile(trackedAccount) || ShouldRotateDeviceProfile(trackedAccount, nowUtc))
        {
            ApplyDeviceProfileSnapshot(trackedAccount, FakeMachineInfo.CreateSnapshot(), nowUtc);
            isChanged = true;
        }

        if (isChanged)
            Save(trackedAccount);

        return CreateDeviceProfileSnapshot(trackedAccount);
    }

    public DeviceProfileSnapshot GetEditableDeviceProfileSnapshot(XIVAccount account)
    {
        var trackedAccount = GetTrackedAccount(account);
        NormalizeDeviceProfileSettings(trackedAccount);
        if (!trackedAccount.DeviceProfileDynamicEnabled)
            return GetSharedDeviceProfileState().ToSnapshot();

        return HasDeviceProfile(trackedAccount) ? CreateDeviceProfileSnapshot(trackedAccount) : FakeMachineInfo.CreateSnapshot();
    }

    public DeviceProfileSnapshot? TryGetSavedDeviceProfileSnapshot(XIVAccount account)
    {
        var trackedAccount = GetTrackedAccount(account);
        NormalizeDeviceProfileSettings(trackedAccount);
        return HasDeviceProfile(trackedAccount) ? CreateDeviceProfileSnapshot(trackedAccount) : null;
    }

    public DeviceProfileSnapshot RefreshDeviceProfile(XIVAccount account)
    {
        var trackedAccount = GetTrackedAccount(account);
        NormalizeDeviceProfileSettings(trackedAccount);

        var snapshot = FakeMachineInfo.CreateSnapshot();
        ApplyDeviceProfileSnapshot(trackedAccount, snapshot, DateTimeOffset.UtcNow);
        Save(trackedAccount);
        return snapshot;
    }

    public long GetSharedDeviceProfileGeneratedUtcTicks() =>
        GetSharedDeviceProfileState().GeneratedUtcTicks;

    public DeviceProfileSnapshot GetSharedDeviceProfileSnapshot() =>
        GetSharedDeviceProfileState().ToSnapshot();

    public void UpdateDeviceProfileSettings(XIVAccount account, bool dynamicEnabled, bool isDeviceProfileRotation, int rotationDays)
    {
        var trackedAccount = GetTrackedAccount(account);

        trackedAccount.DeviceProfileDynamicEnabled = dynamicEnabled;
        trackedAccount.IsDeviceProfileRotation     = isDeviceProfileRotation;
        trackedAccount.DeviceProfileRotationDays   = NormalizeDeviceProfileRotationDays(rotationDays);

        Save(trackedAccount);
    }

    public void SaveDeviceProfileSnapshot(XIVAccount account, DeviceProfileSnapshot snapshot, long generatedUtcTicks)
    {
        var trackedAccount = GetTrackedAccount(account);
        var generatedAtUtc = new DateTimeOffset(generatedUtcTicks, TimeSpan.Zero);

        ApplyDeviceProfileSnapshot(trackedAccount, snapshot, generatedAtUtc);
        Save(trackedAccount);
    }

    public void SaveSharedDeviceProfileSnapshot(DeviceProfileSnapshot snapshot, long generatedUtcTicks)
    {
        var state = new StoredDeviceProfileSnapshot(snapshot, generatedUtcTicks);

        lock (SharedDeviceProfileSyncRoot)
        {
            sharedDeviceProfile = state;
            PersistSharedDeviceProfileState(state);
        }
    }

    public void ApplyResolvedDeviceProfile(XIVAccount account, ResolvedDeviceProfile resolvedDeviceProfile)
    {
        account.DeviceProfileDynamicEnabled = resolvedDeviceProfile.DynamicEnabled;
        account.IsDeviceProfileRotation     = resolvedDeviceProfile.IsRotationEnabled;
        account.DeviceProfileRotationDays   = NormalizeDeviceProfileRotationDays(resolvedDeviceProfile.RotationDays);

        if (!resolvedDeviceProfile.DynamicEnabled)
            return;

        account.DeviceProfileDeviceId              = resolvedDeviceProfile.Snapshot.DeviceId;
        account.DeviceProfileMacAddress            = resolvedDeviceProfile.Snapshot.MacAddress;
        account.DeviceProfileHostName              = resolvedDeviceProfile.Snapshot.HostName;
        account.DeviceProfileLastGeneratedUtcTicks = resolvedDeviceProfile.LastGeneratedUtcTicks;
    }

    public void RemoveAccount(XIVAccount account)
    {
        account.Password = string.Empty;
        ClearUnavailableSecrets(account.Id);
        Accounts.Remove(account);

        lock (syncRoot)
        {

            db.RunInTransaction
            (() =>
                {
                    var record = db.Table<XIVAccount>().FirstOrDefault(a => a.Id == account.Id);
                    if (record != null)
                        db.Delete(account);
                }
            );
        }
    }

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

        var fallbackCred       = GetCredProvider(fallbackType);
        var fallbackSupported  = await fallbackCred.IsSupported();
        if (!fallbackSupported)
            throw new InvalidOperationException($"默认凭据类型 {fallbackType} 当前不可用");

        CredProvider    = fallbackCred;
        CurrentCredType = fallbackType;
        MarkUnavailableSecretsFromExistingAccounts();

        var userMessage = "当前设备不支持 Windows Hello，已自动切换为系统凭据管理器，并关闭自动登录。此前保存的密码或自动登录凭据需要重新输入后再保存。";

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
            WasFallbackApplied: true,
            ShouldDisableAutoLogin: true,
            HasUnavailableSavedSecrets: HasUnavailableSavedSecrets,
            UserMessage: userMessage
        );
    }

    private void MarkUnavailableSecretsFromExistingAccounts()
    {
        unavailableSavedSecretAccountIds.Clear();

        foreach (var account in Accounts.Where(HasStoredSecrets))
            unavailableSavedSecretAccountIds.Add(account.Id);
    }

    private void ClearUnavailableSecrets(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return;

        unavailableSavedSecretAccountIds.Remove(accountId);
    }

    private static bool HasStoredSecrets(XIVAccount account) =>
        !string.IsNullOrWhiteSpace(account.Password)
        || !string.IsNullOrWhiteSpace(account.AutoLoginSessionKey)
        || !string.IsNullOrWhiteSpace(account.TestSID)
        || !string.IsNullOrWhiteSpace(account.NSessionId);

    private ICredProvider GetCredProvider(CredType type) =>
        type switch
        {
            CredType.WindowsCredManager => new CredentialManager(CredData),
            CredType.WindowsHello       => new WindowsHello(CredData),
            CredType.NoEncryption       => new NoCred(CredData),
            _                           => throw new ArgumentOutOfRangeException(nameof(type), type, "未知的凭据类型")
        };

    private void Accounts_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e) =>
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
                    var record = db.Table<XIVAccount>().FirstOrDefault(a => a.Id == account.Id);

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
            if (account.UserName.IsNullOrEmpty() || account.Id.IsNullOrEmpty())
                Accounts.Remove(account);
        }
    }

    private XIVAccount GetTrackedAccount(XIVAccount account) =>
        Accounts.FirstOrDefault(existing => existing.Id == account.Id) ?? account;

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

    private static void ApplyDeviceProfileSnapshot(XIVAccount account, DeviceProfileSnapshot snapshot, DateTimeOffset generatedAtUtc)
    {
        account.DeviceProfileDeviceId              = snapshot.DeviceId;
        account.DeviceProfileMacAddress            = snapshot.MacAddress;
        account.DeviceProfileHostName              = snapshot.HostName;
        account.DeviceProfileLastGeneratedUtcTicks = generatedAtUtc.UtcTicks;
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

    private void MigrateAccountsTable()
    {
        var columns = db!.Query<SQLiteTableColumn>("PRAGMA table_info(\"XIVAccount\")")
                         .Select(column => column.name)
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

        EnsureColumn(columns, "DeviceProfileDeviceId",       "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(columns, "DeviceProfileMacAddress",     "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(columns, "DeviceProfileHostName",       "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(columns, "DeviceProfileDynamicEnabled", "INTEGER NOT NULL DEFAULT 0");
        var addedRotationColumn = EnsureColumn(columns, "IsDeviceProfileRotation", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(columns, "DeviceProfileRotationDays",          $"INTEGER NOT NULL DEFAULT {DEFAULT_DEVICE_PROFILE_ROTATION_DAYS}");
        EnsureColumn(columns, "DeviceProfileLastGeneratedUtcTicks", "INTEGER NOT NULL DEFAULT 0");

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

    private static StoredDeviceProfileSnapshot GetSharedDeviceProfileState()
    {
        lock (SharedDeviceProfileSyncRoot)
        {
            sharedDeviceProfile ??= LoadOrCreateSharedDeviceProfileState();
            return sharedDeviceProfile;
        }
    }

    private static StoredDeviceProfileSnapshot LoadOrCreateSharedDeviceProfileState()
    {
        try
        {
            if (File.Exists(SharedDeviceProfilePath))
            {
                var json   = File.ReadAllText(SharedDeviceProfilePath);
                var stored = JsonSerializer.Deserialize<StoredDeviceProfileSnapshot>(json, SharedDeviceProfileJsonOptions);
                if (stored != null && HasDeviceProfile(stored.ToSnapshot()))
                    return stored;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取共享设备信息失败，将重新生成。");
        }

        var generated = new StoredDeviceProfileSnapshot(FakeMachineInfo.CreateSnapshot(), DateTimeOffset.UtcNow.UtcTicks);
        PersistSharedDeviceProfileState(generated);
        return generated;
    }

    private static void PersistSharedDeviceProfileState(StoredDeviceProfileSnapshot state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SharedDeviceProfilePath)!);
            var json = JsonSerializer.Serialize(state, SharedDeviceProfileJsonOptions);
            File.WriteAllText(SharedDeviceProfilePath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存共享设备信息失败。");
        }
    }

    private sealed class SQLiteTableColumn
    {
        public string name { get; set; } = string.Empty;
    }

    private sealed class StoredDeviceProfileSnapshot
    {
        public string DeviceId { get; init; } = string.Empty;

        public string MacAddress { get; init; } = string.Empty;

        public string HostName { get; init; } = string.Empty;

        public long GeneratedUtcTicks { get; init; }

        public StoredDeviceProfileSnapshot()
        {
        }

        public StoredDeviceProfileSnapshot(DeviceProfileSnapshot snapshot, long generatedUtcTicks)
        {
            DeviceId          = snapshot.DeviceId;
            MacAddress        = snapshot.MacAddress;
            HostName          = snapshot.HostName;
            GeneratedUtcTicks = generatedUtcTicks;
        }

        public DeviceProfileSnapshot ToSnapshot() =>
            new()
            {
                DeviceId   = DeviceId,
                MacAddress = MacAddress,
                HostName   = HostName
            };
    }

    #endregion
}
