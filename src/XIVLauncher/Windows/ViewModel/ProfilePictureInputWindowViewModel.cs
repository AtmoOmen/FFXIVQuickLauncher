using XIVLauncher.Accounts;

namespace XIVLauncher.Windows.ViewModel;

internal class ProfilePictureInputWindowViewModel : ViewModelBase
{
    public string CharacterName
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string WorldName
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public void Load(XIVAccount account)
    {
        CharacterName = account.ChosenCharacterName  ?? string.Empty;
        WorldName     = account.ChosenCharacterWorld ?? string.Empty;
    }
}
