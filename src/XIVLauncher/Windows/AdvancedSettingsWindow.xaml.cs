using System.Windows;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for FirstTimeSetup.xaml
/// </summary>
public partial class AdvancedSettingsWindow
{
    private AdvancedSettingsViewModel ViewModel => (AdvancedSettingsViewModel)DataContext;

    public AdvancedSettingsWindow()
    {
        InitializeComponent();

        DataContext = new AdvancedSettingsViewModel();
        ViewModel.Load();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Save();
        Close();
    }
}
