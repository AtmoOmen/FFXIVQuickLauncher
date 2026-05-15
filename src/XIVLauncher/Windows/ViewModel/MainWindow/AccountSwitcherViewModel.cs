using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using XIVLauncher.Account;
using XIVLauncher.Common.Constant;
using XIVLauncher.Windows.Services;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel.MainWindow;

internal sealed class AccountSwitcherViewModel : INotifyPropertyChanged
{
    public ObservableCollection<AccountSwitcherEntry> Entries { get; } = [];

    private readonly SyncCommand createDesktopShortcutCommand;
    private readonly SyncCommand removeAccountCommand;
    private readonly SyncCommand setProfilePictureCommand;
    private readonly SyncCommand configureDeviceProfileCommand;

    public ICommand CreateDesktopShortcutCommand => createDesktopShortcutCommand;

    public ICommand RemoveAccountCommand => removeAccountCommand;

    public ICommand SetProfilePictureCommand => setProfilePictureCommand;

    public ICommand ConfigureDeviceProfileCommand => configureDeviceProfileCommand;

    public AccountSwitcherEntry? SelectedEntry
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnPropertyChanged(nameof(IsSelectedAccountPasswordNotSaved));
            createDesktopShortcutCommand.RaiseCanExecuteChanged();
            removeAccountCommand.RaiseCanExecuteChanged();
            setProfilePictureCommand.RaiseCanExecuteChanged();
            configureDeviceProfileCommand.RaiseCanExecuteChanged();
        }
    }

    public AccountSwitcherEntry? ContextEntry
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnPropertyChanged(nameof(IsSelectedAccountPasswordNotSaved));
            createDesktopShortcutCommand.RaiseCanExecuteChanged();
            removeAccountCommand.RaiseCanExecuteChanged();
            setProfilePictureCommand.RaiseCanExecuteChanged();
            configureDeviceProfileCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsSelectedAccountPasswordNotSaved
    {
        get => ActiveEntry != null && !HasSavedSecret(ActiveEntry.Account);
        set
        {
            var activeEntry = ActiveEntry;
            if (activeEntry == null)
                return;

            var selectedAccountId = SelectedEntry?.Account.ID;
            var account           = FindTrackedAccount(activeEntry.Account);
            account.QuickLoginEnabled = !value;

            if (value)
            {
                account.SdoPassword       = string.Empty;
                account.WeGameQuickLoginSecret = null;
            }

            accountManager.Save();
            RefreshEntries(selectedAccountId);
        }
    }

    private AccountSwitcherEntry? ActiveEntry => ContextEntry ?? SelectedEntry;

    private readonly AccountManager   accountManager;
    private readonly IDialogService   dialogService;
    private readonly IShortcutService shortcutService;
    private readonly Action?          requestClose;

    public AccountSwitcherViewModel
    (
        AccountManager    accountManager,
        IDialogService?   dialogService   = null,
        IShortcutService? shortcutService = null,
        Action?           requestClose    = null
    )
    {
        this.accountManager  = accountManager;
        this.dialogService   = dialogService   ?? new DialogService();
        this.shortcutService = shortcutService ?? new ShortcutService();
        this.requestClose    = requestClose;

        createDesktopShortcutCommand  = new SyncCommand(_ => CreateDesktopShortcut(),          () => ActiveEntry != null);
        removeAccountCommand          = new SyncCommand(_ => RemoveSelectedAccount(),          () => ActiveEntry != null);
        setProfilePictureCommand      = new SyncCommand(_ => SetSelectedProfilePicture(),      () => ActiveEntry != null);
        configureDeviceProfileCommand = new SyncCommand(_ => ConfigureSelectedDeviceProfile(), () => ActiveEntry != null);
        RefreshEntries();
    }

    public void RefreshEntries(string? selectedAccountId = null, bool useCurrentAccountSelection = true)
    {
        ContextEntry = null;
        if (useCurrentAccountSelection)
            selectedAccountId ??= SelectedEntry?.Account.ID;
        if (string.IsNullOrWhiteSpace(selectedAccountId) && useCurrentAccountSelection && accountManager.HasCurrentAccountSelection)
            selectedAccountId = accountManager.CurrentAccountID;

        Entries.Clear();

        foreach (var account in accountManager.Accounts)
        {
            var entry = new AccountSwitcherEntry { Account = account };

            try
            {
                entry.UpdateProfileImage();
            }
            catch
            {
                // ignored
            }

            Entries.Add(entry);
        }

        SelectedEntry = string.IsNullOrWhiteSpace(selectedAccountId)
                            ? null
                            : Entries.FirstOrDefault(entry => entry.Account.ID == selectedAccountId);
    }

    public XIVAccount? SelectCurrentAccount() =>
        SelectedEntry?.Account;

    public void MoveEntry(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex || fromIndex < 0 || toIndex < 0 || fromIndex >= Entries.Count || toIndex >= Entries.Count)
            return;

        Entries.Move(fromIndex, toIndex);

        accountManager.Accounts.Clear();
        foreach (var entry in Entries)
            accountManager.Accounts.Add(entry.Account);

        accountManager.Save();
        SelectedEntry = Entries[toIndex];
    }

    public void CreateDesktopShortcut()
    {
        var activeEntry = ActiveEntry;
        if (activeEntry == null)
            return;

        try
        {
            var iconPath     = ResolveShortcutIconPath(activeEntry);
            var launcherPath = Paths.ResolveExecutablePath();
            var desktop      = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            shortcutService.CreateShortcut
            (
                desktop,
                $"XIVLauncherCN - {activeEntry.Account.UserName}",
                launcherPath,
                $"使用“{activeEntry.Account.UserName}”账号启动 XIVLauncher。",
                iconPath,
                $"--account={activeEntry.Account.ID}"
            );
        }
        catch (Exception ex)
        {
            dialogService.ShowMessage
            (
                $"创建桌面快捷方式失败。\n{ex.Message}",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
    }

    public void RemoveSelectedAccount()
    {
        var activeEntry = ActiveEntry;
        if (activeEntry == null)
            return;

        var selectedAccountId = SelectedEntry?.Account.ID;
        AccountSwitcherEntry.RemoveCustomProfileImage(activeEntry.Account);
        accountManager.RemoveAccount(activeEntry.Account);
        RefreshEntries(selectedAccountId == activeEntry.Account.ID ? null : selectedAccountId);
    }

    public void SetSelectedProfilePicture()
    {
        var selectedEntry = ActiveEntry;
        if (selectedEntry == null)
            return;

        requestClose?.Invoke();

        if (!dialogService.ShowProfilePictureInput(selectedEntry.Account, out var profileImagePath))
            return;

        var account = FindTrackedAccount(selectedEntry.Account);

        if (string.IsNullOrWhiteSpace(profileImagePath))
            AccountSwitcherEntry.RemoveCustomProfileImage(account);
        else
            AccountSwitcherEntry.SaveCustomProfileImage(account, profileImagePath);

        accountManager.Save();

        RefreshEntries(SelectedEntry?.Account.ID);
    }

    private static bool HasSavedSecret(XIVAccount account) =>
        account.QuickLoginEnabled
        || !string.IsNullOrWhiteSpace(account.SdoPassword)
        || !string.IsNullOrWhiteSpace(account.WeGameQuickLoginSecret)
        || !string.IsNullOrWhiteSpace(account.SdoQuickLoginSecret);

    public void ConfigureSelectedDeviceProfile()
    {
        var selectedEntry = ActiveEntry;
        if (selectedEntry == null)
            return;

        var account = FindTrackedAccount(selectedEntry.Account);
        requestClose?.Invoke();
        var changed = dialogService.ShowAccountDeviceProfileSettings(account, accountManager);
        if (changed)
            RefreshEntries(SelectedEntry?.Account.ID);
    }

    private static Bitmap BitmapSourceToBitmap(BitmapSource bitmapSource)
    {
        using var outputStream = new MemoryStream();

        BitmapEncoder encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
        encoder.Save(outputStream);

        using var bitmap = new Bitmap(outputStream);
        return new Bitmap(bitmap);
    }

    private static void SaveAsIcon(Bitmap sourceBitmap, string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Create);

        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(1);
        stream.WriteByte(0);
        stream.WriteByte(1);
        stream.WriteByte(0);

        stream.WriteByte((byte)sourceBitmap.Width);
        stream.WriteByte((byte)sourceBitmap.Height);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(32);
        stream.WriteByte(0);

        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);

        stream.WriteByte(22);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);

        sourceBitmap.Save(stream, ImageFormat.Png);

        var dataLength = stream.Length - 22;
        stream.Seek(14, SeekOrigin.Begin);
        stream.WriteByte((byte)dataLength);
        stream.WriteByte((byte)(dataLength >> 8));
    }

    private static string ResolveShortcutIconPath(AccountSwitcherEntry entry)
    {
        var launcherPath = Paths.ResolveExecutablePath();

        if (!AccountSwitcherEntry.TryGetCustomProfileImagePath(entry.Account, out var customProfileImagePath))
            return launcherPath;

        if (string.Equals(Path.GetExtension(customProfileImagePath), ".ico", StringComparison.OrdinalIgnoreCase))
            return customProfileImagePath;

        if (entry.ProfileImage is not BitmapSource bitmapSource)
            return launcherPath;

        var iconDirectory = Path.Combine(Paths.RoamingPath, "profileIcons");
        Directory.CreateDirectory(iconDirectory);

        var iconFileName = $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Account.ID)))}.ico";
        var iconPath     = Path.Combine(iconDirectory, iconFileName);
        SaveAsIcon(BitmapSourceToBitmap(bitmapSource), iconPath);
        return iconPath;
    }

    private XIVAccount FindTrackedAccount(XIVAccount account) =>
        accountManager.Accounts.First(existing => existing.ID == account.ID);

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
