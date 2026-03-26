using System.Windows;
using XIVLauncher.Accounts;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for FirstTimeSetup.xaml
/// </summary>
public partial class ProfilePictureInputWindow : Window
{
    public  string                             ResultName  = string.Empty;
    public  string                             ResultWorld = string.Empty;
    private ProfilePictureInputWindowViewModel ViewModel => (ProfilePictureInputWindowViewModel)DataContext;

    public ProfilePictureInputWindow(XIVAccount account)
    {
        InitializeComponent();

        DataContext = new ProfilePictureInputWindowViewModel();
        ViewModel.Load(account);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        ResultName  = ViewModel.CharacterName;
        ResultWorld = ViewModel.WorldName;

        Close();
    }
}
