using System.Windows;
using XIVLauncher.Common.CompanionApp;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

public partial class CompanionAppSetupWindow : Window
{
    public CompanionAppConfiguration? Result { get; private set; }

    private CompanionAppSetupWindowViewModel ViewModel => (CompanionAppSetupWindowViewModel)DataContext;

    public CompanionAppSetupWindow(CompanionAppConfiguration? companionApp = null)
    {
        InitializeComponent();

        DataContext = new CompanionAppSetupWindowViewModel();
        ViewModel.Load(companionApp);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Result = ViewModel.BuildResult();
        Close();
    }
}
