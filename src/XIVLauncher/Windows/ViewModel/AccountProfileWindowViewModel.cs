using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using XIVLauncher.Account;
using XIVLauncher.Common.Game;
using XIVLauncher.Windows.ViewModel.Main.Models;

namespace XIVLauncher.Windows.ViewModel;

internal sealed partial class AccountProfileWindowViewModel : ObservableObject
{
    private XIVAccount selectedAccount = null!;

    [ObservableProperty]
    public partial string AccountDisplayName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string UserDefinedName { get; set; } = string.Empty;

    public string OriginalUserName { get; private set; } = string.Empty;

    public string AreaName { get; private set; } = string.Empty;

    public string AccountType { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    public partial string SelectedFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ImageSource PreviewImage { get; set; } = AccountSwitcherEntry.GetDefaultProfileImage();

    public bool HasSelectedFile =>
        !string.IsNullOrWhiteSpace(SelectedFilePath);

    public void Load
    (
        XIVAccount account
    )
    {
        selectedAccount    = account;
        AccountDisplayName = account.DisplayName;
        UserDefinedName    = account.UserDefinedName ?? string.Empty;
        OriginalUserName   = $"账号: {account.UserName}";
        AreaName           = $"大区: {account.AreaName}";
        AccountType = account.AccountType switch
        {
            XIVAccountType.Sdo    => "盛趣",
            XIVAccountType.WeGame => "WeGame",
            _                     => "未知渠道"
        };

        SelectedFilePath = AccountSwitcherEntry.TryGetCustomProfileImagePath(account, out var imagePath) ?
                               imagePath :
                               string.Empty;

        PreviewImage = AccountSwitcherEntry.GetProfileImage(account);
    }

    public void ApplyChanges()
    {
        var note = UserDefinedName?.Trim();
        selectedAccount.UserDefinedName = string.IsNullOrWhiteSpace(note) ?
                                              null! :
                                              note;
    }

    public void SetPreviewImage
    (
        string imagePath
    )
    {
        SelectedFilePath = imagePath;
        PreviewImage     = AccountSwitcherEntry.LoadProfileImageFromPath(imagePath);
    }

    public void ClearPreviewImage()
    {
        SelectedFilePath = string.Empty;
        PreviewImage     = AccountSwitcherEntry.GetDefaultProfileImage();
    }
}
