using System.Windows.Media;
using XIVLauncher.Accounts;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Windows.ViewModel;

internal sealed class AccountProfileWindowViewModel : ViewModelBase
{
    private XIVAccount selectedAccount = null!;

    public string AccountDisplayName
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public string UserDefinedName
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string OriginalUserName { get; private set; } = string.Empty;

    public string AreaName { get; private set; } = string.Empty;

    public string AccountType { get; private set; } = string.Empty;

    public string SelectedFilePath
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnPropertyChanged(nameof(HasSelectedFile));
        }
    } = string.Empty;

    public ImageSource PreviewImage
    {
        get;
        set => SetProperty(ref field, value);
    } = AccountSwitcherEntry.GetDefaultProfileImage();

    public bool HasSelectedFile => 
        !string.IsNullOrWhiteSpace(SelectedFilePath);

    public void Load(XIVAccount account)
    {
        selectedAccount    = account;
        AccountDisplayName = account.DisplayName;
        UserDefinedName    = account.UserDefinedName ?? string.Empty;
        OriginalUserName   = $"账号: {account.UserName}";
        AreaName           = $"大区: {account.AreaName}";
        AccountType = account.AccountType switch
        {
            XIVAccountType.Sdo                                => "盛趣",
            XIVAccountType.WeGame or XIVAccountType.WeGameSID => "WeGame",
            _                                                 => "未知渠道"
        };

        SelectedFilePath = AccountSwitcherEntry.TryGetCustomProfileImagePath(account, out var imagePath)
                               ? imagePath
                               : string.Empty;

        PreviewImage = AccountSwitcherEntry.GetProfileImage(account);
    }

    public void ApplyChanges()
    {
        var note = UserDefinedName?.Trim();
        selectedAccount.UserDefinedName = string.IsNullOrWhiteSpace(note) ? null! : note;
    }

    public void SetPreviewImage(string imagePath)
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
