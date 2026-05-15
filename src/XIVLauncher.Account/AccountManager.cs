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
using Serilog;
using SQLite;
using XIVLauncher.Account.Cred;
using XIVLauncher.Account.Cred.Providers;
using XIVLauncher.Account.DeviceProfiles;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Account;

public class AccountManager
{
    public const int DEFAULT_DEVICE_PROFILE_ROTATION_DAYS = 7;

    public XIVAccount? CurrentAccount
    {
        get
        {
            var currentAccount = Accounts.FirstOrDefault(account => account.ID == setting.CurrentAccountID) ?? Accounts.FirstOrDefault();

            if (currentAccount != null && !string.Equals(setting.CurrentAccountID, currentAccount.ID, StringComparison.Ordinal))
                setting.CurrentAccountID = currentAccount.ID;

            return currentAccount;
        }
        set => setting.CurrentAccountID = value?.ID ?? string.Empty;
    }

    public ICredProvider CredProvider { get; private set; }

    public CredType CurrentCredType { get; private set; } = DEFAULT_CRED_TYPE;

    public ObservableCollection<XIVAccount> Accounts { get; } = [];

    private SQLiteConnection Database
    {
        get => field ?? throw new InvalidOperationException("数据库尚未初始化");
        set;
    }

    public string CurrentAccountID =>
        setting.CurrentAccountID;

    public bool HasCurrentAccountSelection =>
        !string.IsNullOrWhiteSpace(setting.CurrentAccountID);

    public bool HasUnavailableSavedSecrets =>
        unavailableSavedSecretAccountIds.Count != 0;

    private const CredType DEFAULT_CRED_TYPE = CredType.WindowsCredManager;

    private static readonly string DeviceProfilePresetStorePath  = Path.Combine(Paths.RoamingPath, "deviceProfilePresets.json");
    private static readonly string LegacySharedDeviceProfilePath = Path.Combine(Paths.RoamingPath, "sharedDeviceProfile.json");
    private static readonly string DatabasePath                  = Path.Combine(Paths.RoamingPath, "accounts.db");

    private static readonly JsonSerializerOptions DeviceProfilePresetStoreJsonOptions = new() { WriteIndented = true };

    private readonly IAccountSettingsStore setting;
    private readonly CredData           credData;

    private DeviceProfilePresetStoreState? deviceProfilePresetStore;

    private readonly HashSet<string> unavailableSavedSecretAccountIds = [];

    public AccountManager(IAccountSettingsStore setting)
    {
        this.setting = setting;

        var credPath = Path.Combine(Paths.RoamingPath, "cred.json");
        credData     = new CredData("XIVLauncherCN", credPath);
        CredProvider = GetCredProvider(DEFAULT_CRED_TYPE);

        Load();

        Accounts.CollectionChanged += Accounts_CollectionChanged;
    }

    /// <summary>
    ///     规范化设备预设轮换天数，保证值始终落在有效范围内
    /// </summary>
    /// <param name="rotationDays">待规范化的轮换天数</param>
    /// <returns>有效的轮换天数</returns>
    public static int NormalizeDeviceProfileRotationDays(int rotationDays) =>
        rotationDays < 1 ? DEFAULT_DEVICE_PROFILE_ROTATION_DAYS : rotationDays;

    /// <summary>
    ///     将单个账号写入数据库
    /// </summary>
    /// <param name="account">待保存账号</param>
    public void Save(XIVAccount account)
    {
        Database.RunInTransaction
        (() =>
            {
                var record = Database.Table<XIVAccount>().FirstOrDefault(a => a.ID == account.ID);

                if (record == null)
                    Database.Insert(account);
                else
                    Database.Update(account);
            }
        );
    }

    /// <summary>
    ///     将当前内存中的所有账号写入数据库
    /// </summary>
    public void Save()
    {
        ApplySequentialSortOrder();

        foreach (var item in Accounts)
            Save(item);
    }

    /// <summary>
    ///     初始化数据库连接并执行表结构迁移
    /// </summary>
    public void SetupDb()
    {
        Database = new SQLiteConnection
        (
            DatabasePath,
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex
        );
        Database.CreateTable<XIVAccount>();
        MigrateAccountsTable();
    }

    /// <summary>
    ///     从磁盘加载账号数据并修复旧数据
    /// </summary>
    public void Load()
    {
        try
        {
            SetupDb();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load VFS database, starting fresh");

            try
            {
                Database.Close();
                Database.Dispose();
            }
            catch
            {
                // ignored
            }

            if (File.Exists(DatabasePath))
                File.Delete(DatabasePath);

            SetupDb();
        }

        var storedAccounts = Database.Table<XIVAccount>()
                                     .OrderBy(account => account.SortOrder)
                                     .ThenBy(account => account.Index)
                                     .ToArray();

        Accounts.Clear();
        foreach (var account in storedAccounts)
            Accounts.Add(account);

        foreach (var account in Accounts.ToArray())
        {
            if (string.IsNullOrWhiteSpace(account.UserName) || string.IsNullOrWhiteSpace(account.ID))
                Accounts.Remove(account);
        }

        MigrateLegacyDeviceProfiles();
    }

    /// <summary>
    ///     使用当前凭据提供程序加密文本
    /// </summary>
    /// <param name="text">待加密文本</param>
    /// <returns>加密后的文本，失败时返回 <see langword="null" /></returns>
    public async Task<string?> Encrypt(string? text)
    {
        try
        {
            if (text is null)
                return null;

            return await CredProvider.Encrypt(text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to encrypt text");
        }

        return null;
    }

    /// <summary>
    ///     使用当前凭据提供程序解密文本
    /// </summary>
    /// <param name="text">待解密文本</param>
    /// <returns>解密后的文本，失败时返回 <see langword="null" /></returns>
    public async Task<string?> Decrypt(string? text)
    {
        try
        {
            if (text is null)
                return null;

            return await CredProvider.Decrypt(text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to decrypt text");
        }

        return null;
    }

    /// <summary>
    ///     按启动阶段默认值初始化凭据提供程序
    /// </summary>
    /// <param name="requestedType">请求使用的凭据类型，允许为空</param>
    /// <returns>实际应用结果</returns>
    public Task<CredTypeApplyResult> InitializeCredProviderAsync(CredType? requestedType) =>
        ChangeCredTypeAsync(requestedType.GetValueOrDefault(DEFAULT_CRED_TYPE), true);

    /// <summary>
    ///     判断指定凭据类型在当前设备上是否可用
    /// </summary>
    /// <param name="type">凭据类型</param>
    /// <returns>可用则返回 <see langword="true" /></returns>
    public async Task<bool> IsCredTypeSupportedAsync(CredType type) =>
        await GetCredProvider(type).IsSupported();

    /// <summary>
    ///     切换自动登录使用的凭据类型
    /// </summary>
    /// <param name="requestedType">目标凭据类型</param>
    /// <param name="isStartup">是否处于启动阶段</param>
    /// <returns>切换结果</returns>
    public async Task<CredTypeApplyResult> ChangeCredTypeAsync(CredType requestedType, bool isStartup = false)
    {
        if (requestedType == CurrentCredType)
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
                                         : $"当前设备不支持 {requestedType.GetDisplayName()}。";

            Log.Warning("凭据类型 {CredType} 在当前设备不可用", requestedType.GetDisplayName());

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
            if (!ReferenceEquals(oldCred, newCred))
            {
                var testText  = EncryptionHelper.GetRandomHexString(32);
                var encrypted = await newCred.Encrypt(testText);
                var decrypted = await newCred.Decrypt(encrypted);
                if (testText != decrypted)
                    throw new Exception($"Cred type: {requestedType} test failed");
            }

            Log.Information
            (
                "开始切换自动登录加密方式：{OldCredType} -> {NewCredType}",
                previousCredType.GetDisplayName(),
                requestedType.GetDisplayName()
            );

            foreach (var item in Accounts)
            {
                if (HasUnavailableSecrets(item))
                {
                    Log.Warning("账号 {AccountID} 存在当前会话不可读的旧密文，已跳过迁移", item.ID);
                    continue;
                }

                item.SdoPassword            = await ConvertSecretAsync(oldCred, newCred, item.SdoPassword,            item.ID, "登录密码")   ?? string.Empty;
                item.SdoQuickLoginSecret    = await ConvertSecretAsync(oldCred, newCred, item.SdoQuickLoginSecret, item.ID, "盛趣快速登录凭据") ?? string.Empty;
                item.WeGameQuickLoginSecret = await ConvertSecretAsync(oldCred, newCred, item.WeGameQuickLoginSecret, item.ID, "WeGame 快速登录凭据");
            }

            CurrentCredType = requestedType;
            CredProvider    = newCred;
            Save();

            Log.Information
            (
                "自动登录加密方式切换完成：{OldCredType} -> {NewCredType}",
                previousCredType.GetDisplayName(),
                requestedType.GetDisplayName()
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
            Log.Error(ex, "切换自动登录加密方式失败, 目标：{CredType}", requestedType.GetDisplayName());

            return new CredTypeApplyResult
            (
                false,
                requestedType,
                CurrentCredType,
                HasUnavailableSavedSecrets: HasUnavailableSavedSecrets,
                UserMessage: $"切换到 {requestedType.GetDisplayName()} 失败，请稍后重试。"
            );
        }
    }

    /// <summary>
    ///     判断账号是否持有当前会话不可读取的旧密文
    /// </summary>
    /// <param name="account">账号对象</param>
    /// <returns>存在不可用旧密文则返回 <see langword="true" /></returns>
    public bool HasUnavailableSecrets(XIVAccount? account) =>
        account is not null && unavailableSavedSecretAccountIds.Contains(account.ID);

    /// <summary>
    ///     添加账号或更新同一账号的现有信息
    /// </summary>
    /// <param name="account">待保存账号</param>
    public void AddAccount(XIVAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.UserName) || string.IsNullOrWhiteSpace(account.ID))
            throw new Exception($"UserName:{account.UserName} ID:{account.ID} 不能为空");

        var existingAccount = Accounts.FirstOrDefault(a => a.Equals(account));

        Log.Verbose($"existingAccount: {existingAccount?.ID}");

        if (existingAccount != null)
        {
            Log.Verbose("Updating account...");
            existingAccount.ID                                 = account.ID;
            existingAccount.SdoPassword                        = account.SdoPassword;
            existingAccount.WeGameLoginAccount                 = account.WeGameLoginAccount;
            existingAccount.QuickLoginEnabled                  = account.QuickLoginEnabled;
            existingAccount.SdoQuickLoginSecret                = account.SdoQuickLoginSecret;
            existingAccount.WeGameQuickLoginSecret             = account.WeGameQuickLoginSecret;
            existingAccount.AreaName                           = account.AreaName;
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
        {
            account.SortOrder = Accounts.Count;

            Accounts.Add(account);
        }

        ClearUnavailableSecrets(account.ID);
    }

    /// <summary>
    ///     按用户名和账号类型查找账号
    /// </summary>
    /// <param name="userName">用户名</param>
    /// <param name="accountType">账号类型</param>
    /// <returns>匹配账号，不存在则返回 <see langword="null" /></returns>
    public XIVAccount? FindAccount(string? userName, XIVAccountType accountType)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return null;

        return Accounts.FirstOrDefault
        (account => account.AccountType == accountType && string.Equals(account.UserName, userName, StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     获取设备预设列表，按显示名称升序排列
    /// </summary>
    /// <returns>只读预设列表</returns>
    public IReadOnlyList<DeviceProfilePreset> GetDeviceProfilePresets()
    {
        var state = GetDeviceProfilePresetStoreState();
        return state.Presets
                    .OrderBy(preset => preset.DisplayName, StringComparer.Ordinal)
                    .ToArray();
    }

    /// <summary>
    ///     按预设 ID 查找设备预设
    /// </summary>
    /// <param name="presetId">预设 ID</param>
    /// <returns>匹配预设，不存在则返回 <see langword="null" /></returns>
    public DeviceProfilePreset? FindDeviceProfilePreset(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
            return null;

        return FindPresetById(GetDeviceProfilePresetStoreState(), presetId);
    }

    /// <summary>
    ///     获取共享设备预设
    /// </summary>
    /// <returns>共享设备预设</returns>
    public DeviceProfilePreset GetSharedDeviceProfilePreset() =>
        GetSharedDeviceProfilePreset(GetDeviceProfilePresetStoreState());

    /// <summary>
    ///     按用户名和账号类型解析设备配置
    /// </summary>
    /// <param name="userName">用户名</param>
    /// <param name="accountType">账号类型</param>
    /// <returns>解析后的设备配置</returns>
    public ResolvedDeviceProfile ResolveDeviceProfile(string? userName, XIVAccountType accountType)
    {
        var sharedPreset = GetSharedDeviceProfilePreset();
        var account      = FindAccount(userName, accountType);

        return ResolveDeviceProfile(sharedPreset, account, false);
    }

    /// <summary>
    ///     按账户解析设备配置
    /// </summary>
    /// <param name="account">账号</param>
    /// <returns>解析后的设备配置</returns>
    /// <exception cref="ArgumentNullException">传入的账号为空</exception>
    public ResolvedDeviceProfile ResolveDeviceProfile(XIVAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var sharedPreset     = GetSharedDeviceProfilePreset();
        var trackedAccount   = GetTrackedAccount(account);
        var isTrackedAccount = Accounts.Any(existing => existing.ID == trackedAccount.ID);

        return ResolveDeviceProfile(sharedPreset, trackedAccount, isTrackedAccount);
    }

    /// <summary>
    ///     更新账号的设备预设开关和轮换策略
    /// </summary>
    /// <param name="account">目标账号</param>
    /// <param name="dynamicEnabled">是否启用动态设备预设</param>
    /// <param name="isDeviceProfileRotation">是否启用轮换</param>
    /// <param name="rotationDays">轮换周期天数</param>
    public void UpdateDeviceProfileSettings(XIVAccount account, bool dynamicEnabled, bool isDeviceProfileRotation, int rotationDays)
    {
        var trackedAccount = GetTrackedAccount(account);

        trackedAccount.DeviceProfileDynamicEnabled = dynamicEnabled;
        trackedAccount.IsDeviceProfileRotation     = isDeviceProfileRotation;
        trackedAccount.DeviceProfileRotationDays   = NormalizeDeviceProfileRotationDays(rotationDays);

        Save(trackedAccount);
    }

    /// <summary>
    ///     保存账号使用的设备预设选择
    /// </summary>
    /// <param name="account">目标账号</param>
    /// <param name="snapshot">设备快照</param>
    /// <param name="generatedUtcTicks">生成时间戳</param>
    /// <param name="remark">备注</param>
    /// <returns>实际保存的预设</returns>
    public DeviceProfilePreset SaveDeviceProfileSelection(XIVAccount account, DeviceProfileSnapshot snapshot, long generatedUtcTicks, string? remark)
    {
        var trackedAccount = GetTrackedAccount(account);
        var preset         = ApplyDeviceProfileSelection(trackedAccount, snapshot, generatedUtcTicks, remark);
        Save(trackedAccount);
        return preset;
    }

    /// <summary>
    ///     将设备快照应用到账号，但不直接保存
    /// </summary>
    /// <param name="account">目标账号</param>
    /// <param name="snapshot">设备快照</param>
    /// <param name="generatedUtcTicks">生成时间戳</param>
    /// <param name="remark">备注</param>
    /// <returns>应用后的预设</returns>
    public DeviceProfilePreset ApplyDeviceProfileSelection(XIVAccount account, DeviceProfileSnapshot snapshot, long generatedUtcTicks, string? remark) =>
        AssignPresetToAccount(account, snapshot, NormalizeGeneratedUtcTicks(generatedUtcTicks), remark);

    /// <summary>
    ///     保存共享设备预设选择
    /// </summary>
    /// <param name="snapshot">设备快照</param>
    /// <param name="generatedUtcTicks">生成时间戳</param>
    /// <param name="remark">备注</param>
    /// <returns>实际保存的预设</returns>
    public DeviceProfilePreset SaveSharedDeviceProfileSelection(DeviceProfileSnapshot snapshot, long generatedUtcTicks, string? remark)
    {
        var state  = GetDeviceProfilePresetStoreState();
        var result = FindOrCreatePreset(state, snapshot, NormalizeGeneratedUtcTicks(generatedUtcTicks), remark);

        PersistDeviceProfilePresetStoreState
        (
            new DeviceProfilePresetStoreState
            {
                Version        = state.Version,
                SharedPresetId = result.Preset.Id,
                Presets        = result.State.Presets
            }
        );

        return result.Preset;
    }

    /// <summary>
    ///     保存共享设备预设选择，直接指定预设
    /// </summary>
    /// <param name="preset">目标预设</param>
    /// <returns>实际保存的预设</returns>
    public DeviceProfilePreset SaveSharedDeviceProfileSelection(DeviceProfilePreset preset)
    {
        var state = GetDeviceProfilePresetStoreState();
        var found = FindPresetById(state, preset.Id) ?? throw new InvalidOperationException("目标预设不存在。");

        PersistDeviceProfilePresetStoreState
        (
            new DeviceProfilePresetStoreState
            {
                Version        = state.Version,
                SharedPresetId = found.Id,
                Presets        = state.Presets
            }
        );

        return found;
    }

    /// <summary>
    ///     显式创建新的设备预设
    /// </summary>
    /// <param name="snapshot">设备快照</param>
    /// <param name="generatedUtcTicks">生成时间戳</param>
    /// <param name="remark">备注</param>
    /// <returns>新建的预设</returns>
    public DeviceProfilePreset CreateDeviceProfilePreset(DeviceProfileSnapshot snapshot, long generatedUtcTicks, string? remark)
    {
        var state   = GetDeviceProfilePresetStoreState();
        var created = CreatePreset(snapshot, NormalizeGeneratedUtcTicks(generatedUtcTicks), remark);
        var presets = state.Presets.ToList();
        presets.Insert(0, created);

        PersistDeviceProfilePresetStoreState
        (
            new DeviceProfilePresetStoreState
            {
                Version        = state.Version,
                SharedPresetId = state.SharedPresetId,
                Presets        = presets
            }
        );

        return created;
    }

    /// <summary>
    ///     删除设备预设
    /// </summary>
    /// <param name="presetId">预设 ID</param>
    /// <returns>删除后应继续使用的预设</returns>
    public DeviceProfilePreset DeleteDeviceProfilePreset(string presetId)
    {
        var state         = GetDeviceProfilePresetStoreState();
        var removedPreset = FindPresetById(state, presetId) ?? throw new InvalidOperationException("目标预设不存在。");
        if (state.Presets.Count <= 1)
            throw new InvalidOperationException("至少需要保留一个预设。");

        var presets = state.Presets
                           .Where(preset => !string.Equals(preset.Id, presetId, StringComparison.Ordinal))
                           .ToList();

        var replacementPreset = presets.FirstOrDefault(preset => preset.Matches(removedPreset.ToSnapshot()))
                                ?? (string.Equals(state.SharedPresetId, presetId, StringComparison.Ordinal)
                                        ? presets[0]
                                        : FindPresetById(state, state.SharedPresetId) ?? presets[0]);

        PersistDeviceProfilePresetStoreState
        (
            new DeviceProfilePresetStoreState
            {
                Version = state.Version,
                SharedPresetId = string.Equals(state.SharedPresetId, presetId, StringComparison.Ordinal)
                                     ? replacementPreset.Id
                                     : state.SharedPresetId,
                Presets = presets
            }
        );

        foreach (var account in Accounts.Where(account => string.Equals(account.DeviceProfilePresetId, presetId, StringComparison.Ordinal)).ToArray())
        {
            account.DeviceProfilePresetId              = replacementPreset.Id;
            account.DeviceProfileLastGeneratedUtcTicks = replacementPreset.GeneratedUtcTicks;
            ClearLegacyDeviceProfileSnapshot(account);
            Save(account);
        }

        return replacementPreset;
    }

    /// <summary>
    ///     保存账号设备预设选择，直接指定预设
    /// </summary>
    /// <param name="account">目标账号</param>
    /// <param name="preset">目标预设</param>
    /// <returns>实际保存的预设</returns>
    public DeviceProfilePreset SaveDeviceProfileSelection(XIVAccount account, DeviceProfilePreset preset)
    {
        var trackedAccount = GetTrackedAccount(account);
        var found          = FindDeviceProfilePreset(preset.Id) ?? throw new InvalidOperationException("目标预设不存在。");

        trackedAccount.DeviceProfilePresetId              = found.Id;
        trackedAccount.DeviceProfileLastGeneratedUtcTicks = found.GeneratedUtcTicks;
        ClearLegacyDeviceProfileSnapshot(trackedAccount);
        Save(trackedAccount);

        return found;
    }

    /// <summary>
    ///     将解析后的设备配置回写到账号
    /// </summary>
    /// <param name="account">目标账号</param>
    /// <param name="resolvedDeviceProfile">解析结果</param>
    public static void ApplyResolvedDeviceProfile(XIVAccount account, ResolvedDeviceProfile resolvedDeviceProfile)
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

    /// <summary>
    ///     删除账号并清理关联数据
    /// </summary>
    /// <param name="account">待删除账号</param>
    public void RemoveAccount(XIVAccount account)
    {
        account.SdoPassword       = string.Empty;
        account.WeGameQuickLoginSecret = null;
        ClearUnavailableSecrets(account.ID);
        Accounts.Remove(account);

        if (string.Equals(setting.CurrentAccountID, account.ID, StringComparison.Ordinal))
            setting.CurrentAccountID = string.Empty;

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
            var state = GetDeviceProfilePresetStoreState();

            if (!string.Equals(state.SharedPresetId, account.DeviceProfilePresetId, StringComparison.Ordinal)
                && Accounts.All(existing => !string.Equals(existing.DeviceProfilePresetId, account.DeviceProfilePresetId, StringComparison.Ordinal)))
            {
                var presets = state.Presets
                                   .Where(preset => !string.Equals(preset.Id, account.DeviceProfilePresetId, StringComparison.Ordinal))
                                   .ToList();

                PersistDeviceProfilePresetStoreState
                (
                    new DeviceProfilePresetStoreState
                    {
                        Version        = state.Version,
                        SharedPresetId = state.SharedPresetId,
                        Presets        = presets
                    }
                );
            }
        }

        Database.RunInTransaction
        (() =>
            {
                var record = Database.Table<XIVAccount>().FirstOrDefault(a => a.ID == account.ID);
                if (record != null)
                    Database.Delete(record);
            }
        );
    }

    /// <summary>
    ///     清空当前账号选择
    /// </summary>
    public void ClearCurrentAccount() =>
        setting.CurrentAccountID = string.Empty;

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
            requestedType.GetDisplayName(),
            fallbackType.GetDisplayName()
        );

        return new CredTypeApplyResult
        (
            true,
            requestedType,
            fallbackType,
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
        || !string.IsNullOrWhiteSpace(account.SdoQuickLoginSecret)
        || !string.IsNullOrWhiteSpace(account.WeGameQuickLoginSecret);

    private ICredProvider GetCredProvider(CredType type) =>
        type switch
        {
            CredType.WindowsCredManager => new CredentialManager(credData),
            CredType.WindowsHello       => new WindowsHello(credData),
            CredType.NoEncryption       => new NoCred(),
            _                           => throw new ArgumentOutOfRangeException(nameof(type), type, "未知的凭据类型")
        };

    private void Accounts_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Save();

    private XIVAccount GetTrackedAccount(XIVAccount account) =>
        Accounts.FirstOrDefault(existing => existing.ID == account.ID) ?? account;

    private static async Task<string?> ConvertSecretAsync(ICredProvider oldCred, ICredProvider newCred, string? secret, string accountId, string secretName)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return secret;

        try
        {
            var plainText = await oldCred.Decrypt(secret);
            if (plainText is null)
                throw new InvalidOperationException($"账号 {accountId} 的 {secretName} 解密结果为空");

            return await newCred.Encrypt(plainText)
                   ?? throw new InvalidOperationException($"账号 {accountId} 的 {secretName} 加密结果为空");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "迁移账号 {AccountID} 的 {SecretName} 失败", accountId, secretName);
            return secret;
        }
    }

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
        _ = GetDeviceProfilePresetStoreState();

        foreach (var account in Accounts.ToArray())
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
        var columns = Database.Query<SQLiteTableColumn>("PRAGMA table_info(\"XIVAccount\")")
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
        var addedWeGameLoginAccount = EnsureColumn(columns, "WeGameLoginAccount", "TEXT NOT NULL DEFAULT ''");
        var addedSortOrder          = EnsureColumn(columns, "SortOrder", "INTEGER NOT NULL DEFAULT 0");

        if (addedRotationColumn && columns.Contains("DeviceProfileRotationMode"))
            Database.Execute("UPDATE \"XIVAccount\" SET \"IsDeviceProfileRotation\" = CASE WHEN \"DeviceProfileRotationMode\" = 0 THEN 1 ELSE 0 END");

        if (addedWeGameLoginAccount)
            Database.Execute("UPDATE \"XIVAccount\" SET \"WeGameLoginAccount\" = COALESCE(\"LoginAccount\", '') WHERE \"AccountType\" = 1");

        Database.Execute("UPDATE \"XIVAccount\" SET \"WeGameLoginAccount\" = COALESCE(\"LoginAccount\", '') WHERE \"AccountType\" = 1 AND (\"WeGameLoginAccount\" IS NULL OR \"WeGameLoginAccount\" = '')");

        if (addedSortOrder)
            Database.Execute("UPDATE \"XIVAccount\" SET \"SortOrder\" = COALESCE(\"index\", 0)");
    }

    private bool EnsureColumn(HashSet<string> columns, string columnName, string definition)
    {
        if (columns.Contains(columnName))
            return false;

        Database.Execute($"ALTER TABLE \"XIVAccount\" ADD COLUMN \"{columnName}\" {definition}");
        columns.Add(columnName);
        return true;
    }

    private void ApplySequentialSortOrder()
    {
        for (var i = 0; i < Accounts.Count; i++)
            Accounts[i].SortOrder = i;
    }

    private static bool HasDeviceProfile(DeviceProfileSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.DeviceId)
        && !string.IsNullOrWhiteSpace(snapshot.MacAddress)
        && !string.IsNullOrWhiteSpace(snapshot.HostName);

    private DeviceProfilePresetStoreState GetDeviceProfilePresetStoreState()
    {
        deviceProfilePresetStore ??= LoadOrCreateDeviceProfilePresetStoreState();
        return deviceProfilePresetStore ?? throw new InvalidOperationException("设备预设状态尚未初始化");
    }

    private DeviceProfilePresetStoreState LoadOrCreateDeviceProfilePresetStoreState()
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
                    deviceProfilePresetStore = normalized;
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

        deviceProfilePresetStore = state;
        PersistDeviceProfilePresetStoreState(state);
        TryDeleteLegacySharedDeviceProfileFile();
        return state;
    }

    private static DeviceProfilePresetStoreState? NormalizeDeviceProfilePresetStoreState(DeviceProfilePresetStoreState? state)
    {
        if (state is null)
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
            Presets        = presets.ToArray()
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

    private void PersistDeviceProfilePresetStoreState(DeviceProfilePresetStoreState state)
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

    private DeviceProfilePreset GetSharedDeviceProfilePreset(DeviceProfilePresetStoreState state)
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
                Presets        = state.Presets.ToArray()
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
        var state  = GetDeviceProfilePresetStoreState();
        var result = FindOrCreatePreset(state, snapshot, NormalizeGeneratedUtcTicks(generatedUtcTicks), remark);
        PersistDeviceProfilePresetStoreState(result.State);
        return result.Preset;
    }

    private static (DeviceProfilePresetStoreState State, DeviceProfilePreset Preset) FindOrCreatePreset
        (DeviceProfilePresetStoreState state, DeviceProfileSnapshot snapshot, long generatedUtcTicks, string? remark = null)
    {
        var presets       = state.Presets.ToList();
        var existingIndex = presets.FindIndex(preset => preset.Matches(snapshot));

        if (existingIndex >= 0)
        {
            var existing              = presets[existingIndex];
            var normalizedRemark      = remark == null ? existing.Remark : NormalizePresetRemark(remark);
            var updatedGeneratedTicks = Math.Max(existing.GeneratedUtcTicks, generatedUtcTicks);

            if (existing.GeneratedUtcTicks == updatedGeneratedTicks
                && string.Equals(existing.Remark, normalizedRemark, StringComparison.Ordinal))
                return (state, existing);

            var updated = existing.WithGeneratedUtcTicks(updatedGeneratedTicks, normalizedRemark);
            presets[existingIndex] = updated;
            var updatedState = new DeviceProfilePresetStoreState
            {
                Version        = state.Version,
                SharedPresetId = state.SharedPresetId,
                Presets        = presets
            };
            return (updatedState, updated);
        }

        var created = CreatePreset(snapshot, generatedUtcTicks, remark);
        presets.Add(created);
        var createdState = new DeviceProfilePresetStoreState
        {
            Version        = state.Version,
            SharedPresetId = state.SharedPresetId,
            Presets        = presets
        };
        return (createdState, created);
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
    
    private sealed class SQLiteTableColumn
    {
        public string name { get; set; } = string.Empty;
    }
}
