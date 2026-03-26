using System.Windows.Media;
using XIVLauncher.Accounts;

namespace XIVLauncher.Windows.ViewModel;

internal sealed class ProfilePictureInputWindowViewModel : ViewModelBase
{
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

    public bool HasSelectedFile => !string.IsNullOrWhiteSpace(SelectedFilePath);

    public void Load(XIVAccount account)
    {
        SelectedFilePath = AccountSwitcherEntry.TryGetCustomProfileImagePath(account, out var imagePath)
                               ? imagePath
                               : string.Empty;

        PreviewImage = AccountSwitcherEntry.GetProfileImage(account);
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
