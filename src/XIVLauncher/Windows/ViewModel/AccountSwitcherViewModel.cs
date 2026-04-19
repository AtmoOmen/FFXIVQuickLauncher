using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XIVLauncher.Accounts;
using XIVLauncher.Common.Constant;
using XIVLauncher.Windows.Services;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel;

internal sealed class AccountSwitcherViewModel : ViewModelBase
{
    public ObservableCollection<AccountSwitcherEntry> Entries { get; } = [];

    public ICommand CreateDesktopShortcutCommand { get; }

    public ICommand RemoveAccountCommand { get; }

    public ICommand SetProfilePictureCommand { get; }

    public ICommand SetNoteCommand { get; }

    public ICommand ConfigureDeviceProfileCommand { get; }

    public AccountSwitcherEntry? SelectedEntry
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnPropertyChanged(nameof(IsSelectedAccountPasswordNotSaved));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsSelectedAccountPasswordNotSaved
    {
        get => SelectedEntry != null && !SelectedEntry.Account.AutoLogin;
        set
        {
            if (SelectedEntry == null)
                return;

            var account = FindTrackedAccount(SelectedEntry.Account);
            account.AutoLogin = !value;

            if (value)
                account.Password = string.Empty;

            _accountManager.Save();
            RefreshEntries(account.Id);
        }
    }

    private readonly AccountManager        _accountManager;
    private readonly IDialogService        _dialogService;
    private readonly IShortcutService      _shortcutService;
    private readonly Action?               _requestClose;

    public AccountSwitcherViewModel(AccountManager accountManager, IDialogService? dialogService = null, IShortcutService? shortcutService = null, Action? requestClose = null)
    {
        _accountManager  = accountManager;
        _dialogService   = dialogService   ?? new DialogService();
        _shortcutService = shortcutService ?? new ShortcutService();
        _requestClose    = requestClose;

        CreateDesktopShortcutCommand = new SyncCommand(_ => CreateDesktopShortcut(),     () => SelectedEntry != null);
        RemoveAccountCommand         = new SyncCommand(_ => RemoveSelectedAccount(),     () => SelectedEntry != null);
        SetProfilePictureCommand     = new SyncCommand(_ => SetSelectedProfilePicture(), () => SelectedEntry != null);
        SetNoteCommand               = new SyncCommand(_ => SetSelectedNote(),           () => SelectedEntry != null);
        ConfigureDeviceProfileCommand = new SyncCommand(_ => ConfigureSelectedDeviceProfile(), () => SelectedEntry != null);
        RefreshEntries();
    }

    public void RefreshEntries(string? selectedAccountId = null)
    {
        selectedAccountId ??= SelectedEntry?.Account.Id ?? _accountManager.CurrentAccount?.Id;

        Entries.Clear();

        foreach (var account in _accountManager.Accounts)
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

        SelectedEntry = Entries.FirstOrDefault(entry => entry.Account.Id == selectedAccountId) ?? Entries.FirstOrDefault();
    }

    public XIVAccount? SelectCurrentAccount() =>
        SelectedEntry?.Account;

    public void MoveEntry(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex || fromIndex < 0 || toIndex < 0 || fromIndex >= Entries.Count || toIndex >= Entries.Count)
            return;

        Entries.Move(fromIndex, toIndex);

        _accountManager.Accounts.Clear();
        foreach (var entry in Entries)
            _accountManager.Accounts.Add(entry.Account);

        _accountManager.Save();
        SelectedEntry = Entries[toIndex];
    }

    public void CreateDesktopShortcut()
    {
        if (SelectedEntry == null)
            return;

        try
        {
            var iconPath     = ResolveShortcutIconPath(SelectedEntry);
            var launcherPath = Paths.ResolveExecutablePath();
            var desktop      = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            _shortcutService.CreateShortcut
            (
                desktop,
                $"XIVLauncherCN - {SelectedEntry.Account.UserName}",
                launcherPath,
                $"使用“{SelectedEntry.Account.UserName}”账号启动 XIVLauncher。",
                iconPath,
                $"--account={SelectedEntry.Account.Id}"
            );
        }
        catch (Exception ex)
        {
            _dialogService.ShowMessage
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
        if (SelectedEntry == null)
            return;

        var accountId = SelectedEntry.Account.Id;
        _accountManager.RemoveAccount(SelectedEntry.Account);
        RefreshEntries(accountId);
    }

    public void SetSelectedProfilePicture()
    {
        var selectedEntry = SelectedEntry;
        if (selectedEntry == null)
            return;

        _requestClose?.Invoke();

        if (!_dialogService.ShowProfilePictureInput(selectedEntry.Account, out var profileImagePath))
            return;

        var account = FindTrackedAccount(selectedEntry.Account);

        if (string.IsNullOrWhiteSpace(profileImagePath))
            AccountSwitcherEntry.RemoveCustomProfileImage(account);
        else
            AccountSwitcherEntry.SaveCustomProfileImage(account, profileImagePath);

        _accountManager.Save();

        RefreshEntries(account.Id);
    }

    public void SetSelectedNote()
    {
        var selectedEntry = SelectedEntry;
        if (selectedEntry == null)
            return;

        var account = FindTrackedAccount(selectedEntry.Account);
        _requestClose?.Invoke();
        var result = _dialogService.ShowTextInput
        (
            "请输入账号备注。留空时将显示原始账号名。",
            "设置备注",
            account.UserDefinedName ?? string.Empty
        );

        if (result == null)
            return;

        var note = result.Trim();
        account.UserDefinedName = string.IsNullOrWhiteSpace(note) ? null! : note;
        _accountManager.Save();

        RefreshEntries(account.Id);
    }

    public void ConfigureSelectedDeviceProfile()
    {
        var selectedEntry = SelectedEntry;
        if (selectedEntry == null)
            return;

        var account = FindTrackedAccount(selectedEntry.Account);
        _requestClose?.Invoke();
        var changed = _dialogService.ShowAccountDeviceProfileSettings(account, _accountManager);
        if (changed)
            RefreshEntries(account.Id);
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

        var iconFileName = $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Account.Id)))}.ico";
        var iconPath = Path.Combine(iconDirectory, iconFileName);
        SaveAsIcon(BitmapSourceToBitmap(bitmapSource), iconPath);
        return iconPath;
    }

    private XIVAccount FindTrackedAccount(XIVAccount account) =>
        _accountManager.Accounts.First(existing => existing.Id == account.Id);
}
