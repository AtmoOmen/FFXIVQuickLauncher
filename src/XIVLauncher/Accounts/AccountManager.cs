using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Castle.Core.Internal;
using Serilog;
using SQLite;
using XIVLauncher.Accounts.Cred;
using XIVLauncher.Accounts.Cred.CredProviders;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Settings;
using XIVLauncher.Windows;

namespace XIVLauncher.Accounts;

public class AccountManager
{
    public const int DefaultDeviceProfileRotationDays = 7;

    public ObservableCollection<XIVAccount> Accounts;

    public XIVAccount CurrentAccount
    {
        get => Accounts.Count > 1 ? Accounts.FirstOrDefault(a => a.Id == _setting.CurrentAccountId) : Accounts.FirstOrDefault();
        set => _setting.CurrentAccountId = value.Id;
    }

    public           ICredProvider CredProvider { get; private set; }
    private readonly object        syncRoot = new();

    private readonly ILauncherSettingsV3 _setting;

    private readonly CredData CredData;

    private SQLiteConnection? db;
    private CredType?         CurrentCredType;

    public AccountManager(ILauncherSettingsV3 setting)
    {
        Load();

        _setting = setting;

        var credPath = Path.Combine(Paths.RoamingPath, "cred.json");
        CredData = new CredData("XIVLauncherCN", credPath);

        Accounts.CollectionChanged += Accounts_CollectionChanged;
        ChangeCredType(setting.CredType.GetValueOrDefault(CredType.WindowsCredManager));
    }

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

    public async void ChangeCredType(CredType? type)
    {
        if (type == CurrentCredType)
            return;
        var oldCred     = CredProvider;
        var newCred     = GetCredProvider(type.Value);
        var isSupported = await newCred.IsSupported();
        if (!isSupported)
            throw new Exception($"Cred type: {type} not supported");

        if (oldCred != null)
        {
            var testText  = EncryptionHelper.GetRandomHexString(32);
            var encrypted = await newCred.Encrypt(testText);
            var decrypted = await newCred.Decrypt(encrypted);
            if (testText != decrypted)
                throw new Exception($"Cred type: {type} test failed");
        }

        if (oldCred == null)
        {
            CurrentCredType = type;
            CredProvider    = newCred;
            return;
        }

        Log.Information($"Change cred type from {CurrentCredType} to {type}");

        foreach (var item in Accounts)
        {
            if (item.AutoLoginSessionKey != null)
            {
                try
                {
                    var sessionKey = await oldCred.Decrypt(item.AutoLoginSessionKey);
                    item.AutoLoginSessionKey = await newCred.Encrypt(sessionKey);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Failed to change {item.Id}.AutoLoginSessionKey");
                }
            }

            if (item.TestSID != null)
            {
                try
                {
                    var testSid = await oldCred.Decrypt(item.TestSID);
                    item.TestSID = await newCred.Encrypt(testSid);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Failed to change {item.Id}.TestSID");
                }
            }

            if (item.NSessionId != null)
            {
                try
                {
                    var nSessionId = await oldCred.Decrypt(item.NSessionId);
                    item.NSessionId = await newCred.Encrypt(nSessionId);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Failed to change {item.Id}.NSessionId");
                }
            }
        }

        CurrentCredType = type;
        CredProvider    = newCred;
        Log.Information($"Changed cred type from {CurrentCredType} to {type} successfully");
        Save();
    }

    public void AddAccount(XIVAccount account)
    {
        if (account.UserName.IsNullOrEmpty() || account.Id.IsNullOrEmpty())
            throw new Exception($"UserName:{account.UserName} Id:{account.Id} 不能为空");

        var existingAccount = Accounts.FirstOrDefault(a => a.Equals(account));

        Log.Verbose($"existingAccount: {existingAccount?.Id}");

        if (existingAccount != null)
        {
            Log.Verbose("Updating account...");
            existingAccount.Id                  = account.Id;
            existingAccount.Password            = account.Password;
            existingAccount.AutoLogin           = account.AutoLogin;
            existingAccount.AutoLoginSessionKey = account.AutoLoginSessionKey;
            existingAccount.TestSID             = account.TestSID;
            existingAccount.AreaName            = account.AreaName;
            existingAccount.NSessionId          = account.NSessionId;
            existingAccount.DeviceProfileDeviceId         = account.DeviceProfileDeviceId;
            existingAccount.DeviceProfileMacAddress       = account.DeviceProfileMacAddress;
            existingAccount.DeviceProfileHostName         = account.DeviceProfileHostName;
            existingAccount.DeviceProfileDynamicEnabled   = account.DeviceProfileDynamicEnabled;
            existingAccount.DeviceProfileRotationMode     = account.DeviceProfileRotationMode;
            existingAccount.DeviceProfileRotationDays     = NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays);
            existingAccount.DeviceProfileLastGeneratedUtcTicks = account.DeviceProfileLastGeneratedUtcTicks;
        }
        else
            Accounts.Add(account);
    }

    public XIVAccount? FindAccount(string? userName, XIVAccountType accountType)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return null;

        return Accounts.FirstOrDefault
        (
            account => account.AccountType == accountType && string.Equals(account.UserName, userName, StringComparison.Ordinal)
        );
    }

    public ResolvedDeviceProfile ResolveDeviceProfile(string? userName, XIVAccountType accountType)
    {
        var account = FindAccount(userName, accountType);
        if (account == null)
        {
            return new ResolvedDeviceProfile
            (
                FakeMachineInfo.CreateSnapshot(),
                false,
                DeviceProfileRotationMode.Periodic,
                DefaultDeviceProfileRotationDays,
                DateTimeOffset.UtcNow.UtcTicks
            );
        }

        var snapshot = GetOrCreateDeviceProfileSnapshot(account);
        return new ResolvedDeviceProfile
        (
            snapshot,
            account.DeviceProfileDynamicEnabled,
            account.DeviceProfileRotationMode,
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

    public DeviceProfileSnapshot RefreshDeviceProfile(XIVAccount account)
    {
        var trackedAccount = GetTrackedAccount(account);
        NormalizeDeviceProfileSettings(trackedAccount);

        var snapshot = FakeMachineInfo.CreateSnapshot();
        ApplyDeviceProfileSnapshot(trackedAccount, snapshot, DateTimeOffset.UtcNow);
        Save(trackedAccount);
        return snapshot;
    }

    public void UpdateDeviceProfileSettings(XIVAccount account, bool dynamicEnabled, DeviceProfileRotationMode rotationMode, int rotationDays)
    {
        var trackedAccount = GetTrackedAccount(account);

        trackedAccount.DeviceProfileDynamicEnabled = dynamicEnabled;
        trackedAccount.DeviceProfileRotationMode   = NormalizeDeviceProfileRotationMode(rotationMode);
        trackedAccount.DeviceProfileRotationDays   = NormalizeDeviceProfileRotationDays(rotationDays);

        Save(trackedAccount);
    }

    public void SaveDeviceProfileSnapshot(XIVAccount account, DeviceProfileSnapshot snapshot, long generatedUtcTicks)
    {
        var trackedAccount   = GetTrackedAccount(account);
        var generatedAtUtc   = new DateTimeOffset(generatedUtcTicks, TimeSpan.Zero);

        ApplyDeviceProfileSnapshot(trackedAccount, snapshot, generatedAtUtc);
        Save(trackedAccount);
    }

    public void ApplyResolvedDeviceProfile(XIVAccount account, ResolvedDeviceProfile resolvedDeviceProfile)
    {
        account.DeviceProfileDynamicEnabled = resolvedDeviceProfile.DynamicEnabled;
        account.DeviceProfileRotationMode   = NormalizeDeviceProfileRotationMode(resolvedDeviceProfile.RotationMode);
        account.DeviceProfileRotationDays   = NormalizeDeviceProfileRotationDays(resolvedDeviceProfile.RotationDays);
        account.DeviceProfileDeviceId       = resolvedDeviceProfile.Snapshot.DeviceId;
        account.DeviceProfileMacAddress     = resolvedDeviceProfile.Snapshot.MacAddress;
        account.DeviceProfileHostName       = resolvedDeviceProfile.Snapshot.HostName;
        account.DeviceProfileLastGeneratedUtcTicks = resolvedDeviceProfile.LastGeneratedUtcTicks;
    }

    public static int NormalizeDeviceProfileRotationDays(int rotationDays) =>
        rotationDays < 1 ? DefaultDeviceProfileRotationDays : rotationDays;

    public void RemoveAccount(XIVAccount account)
    {
        account.Password = string.Empty;
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

    private ICredProvider GetCredProvider(CredType type)
    {
        switch (type)
        {
            case CredType.WindowsCredManager:
                return new CredentialManager(CredData);

            case CredType.WindowsHello:
                return new WindowsHello(CredData);

            case CredType.NoEncryption:
                return new NoCred(CredData);
        }

        return null;
    }

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
        account.DeviceProfileDeviceId             = snapshot.DeviceId;
        account.DeviceProfileMacAddress           = snapshot.MacAddress;
        account.DeviceProfileHostName             = snapshot.HostName;
        account.DeviceProfileLastGeneratedUtcTicks = generatedAtUtc.UtcTicks;
    }

    private static bool ShouldRotateDeviceProfile(XIVAccount account, DateTimeOffset nowUtc)
    {
        if (!account.DeviceProfileDynamicEnabled || NormalizeDeviceProfileRotationMode(account.DeviceProfileRotationMode) != DeviceProfileRotationMode.Periodic)
            return false;

        if (account.DeviceProfileLastGeneratedUtcTicks <= 0)
            return true;

        var lastGeneratedUtc = new DateTimeOffset(account.DeviceProfileLastGeneratedUtcTicks, TimeSpan.Zero);
        var interval         = TimeSpan.FromDays(NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays));
        return nowUtc - lastGeneratedUtc >= interval;
    }

    private static bool NormalizeDeviceProfileSettings(XIVAccount account)
    {
        var normalizedRotationMode = NormalizeDeviceProfileRotationMode(account.DeviceProfileRotationMode);
        var normalizedRotationDays = NormalizeDeviceProfileRotationDays(account.DeviceProfileRotationDays);
        var isChanged              = false;

        if (account.DeviceProfileRotationMode != normalizedRotationMode)
        {
            account.DeviceProfileRotationMode = normalizedRotationMode;
            isChanged                         = true;
        }

        if (account.DeviceProfileRotationDays != normalizedRotationDays)
        {
            account.DeviceProfileRotationDays = normalizedRotationDays;
            isChanged                         = true;
        }

        return isChanged;
    }

    private static DeviceProfileRotationMode NormalizeDeviceProfileRotationMode(DeviceProfileRotationMode rotationMode) =>
        Enum.IsDefined(typeof(DeviceProfileRotationMode), rotationMode) ? rotationMode : DeviceProfileRotationMode.Periodic;

    private void MigrateAccountsTable()
    {
        var columns = db!.Query<SQLiteTableColumn>("PRAGMA table_info(\"XIVAccount\")")
                         .Select(column => column.name)
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

        EnsureColumn(columns, "DeviceProfileDeviceId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(columns, "DeviceProfileMacAddress", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(columns, "DeviceProfileHostName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(columns, "DeviceProfileDynamicEnabled", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(columns, "DeviceProfileRotationMode", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(columns, "DeviceProfileRotationDays", $"INTEGER NOT NULL DEFAULT {DefaultDeviceProfileRotationDays}");
        EnsureColumn(columns, "DeviceProfileLastGeneratedUtcTicks", "INTEGER NOT NULL DEFAULT 0");
    }

    private void EnsureColumn(ISet<string> columns, string columnName, string definition)
    {
        if (columns.Contains(columnName))
            return;

        db!.Execute($"ALTER TABLE \"XIVAccount\" ADD COLUMN \"{columnName}\" {definition}");
        columns.Add(columnName);
    }

    private sealed class SQLiteTableColumn
    {
        public string name { get; set; } = string.Empty;
    }

    #endregion
}
