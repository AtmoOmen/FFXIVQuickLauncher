using System.Windows;
using XIVLauncher.Common.Addon.Implementations;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for FirstTimeSetup.xaml
/// </summary>
public partial class GenericAddonSetupWindow : Window
{
    public  GenericAddon?                    Result    { get; private set; }
    private GenericAddonSetupWindowViewModel ViewModel => (GenericAddonSetupWindowViewModel)DataContext;

    public GenericAddonSetupWindow(GenericAddon? addon = null)
    {
        InitializeComponent();

        DataContext = new GenericAddonSetupWindowViewModel();
        ViewModel.Load(addon);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        Result = ViewModel.BuildResult();
        Close();
    }
}
