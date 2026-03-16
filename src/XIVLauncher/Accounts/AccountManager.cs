using System;
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
using XIVLauncher.Settings;
using XIVLauncher.Windows;

namespace XIVLauncher.Accounts;

public class AccountManager
{
    public ObservableCollection<XivAccount> Accounts;

    public XivAccount CurrentAccount
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

    public void AddAccount(XivAccount account)
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
        }
        else
            Accounts.Add(account);
    }

    public void RemoveAccount(XivAccount account)
    {
        account.Password = string.Empty;
        Accounts.Remove(account);

        lock (syncRoot)
        {

            db.RunInTransaction
            (() =>
                {
                    var record = db.Table<XivAccount>().FirstOrDefault(a => a.Id == account.Id);
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

    public void Save(XivAccount account)
    {
        lock (syncRoot)
        {

            db.RunInTransaction
            (() =>
                {
                    var record = db.Table<XivAccount>().FirstOrDefault(a => a.Id == account.Id);

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
        db.CreateTable<XivAccount>();
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
        Accounts ??= new ObservableCollection<XivAccount>(db.Table<XivAccount>());

        foreach (var account in Accounts)
        {
            if (account.UserName.IsNullOrEmpty() || account.Id.IsNullOrEmpty())
                Accounts.Remove(account);
        }
    }

    #endregion
}
