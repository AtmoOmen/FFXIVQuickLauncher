using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for SettingsControl.xaml
/// </summary>
public partial class SettingsWindow
{
    private SettingsWindowViewModel ViewModel => (SettingsWindowViewModel)DataContext;

    public SettingsWindow(SettingsWindowViewModel viewModel)
    {
        viewModel.ReloadFromSettings();

        InitializeComponent();
        DataContext                            = viewModel;
        CompanionAppListView.ContextMenu?.DataContext = viewModel;

        DiscordButton.Click += (_, _) => Process.Start(new ProcessStartInfo(Links.DISCORD_URL) { UseShellExecute = true });
    }

    private async void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await ViewModel.SaveToSettingsAsync())
                return;

            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置失败");
            CustomMessageBox.Show
            (
                $"保存设置失败：{ex.Message}",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                parentWindow: this
            );
        }
    }

    private void CompanionAppListView_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        ViewModel.EditSelectedCompanionApp();
    }

    private void CompanionAppListView_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource) is not { } listViewItem)
            return;

        CompanionAppListView.SelectedItem = listViewItem.DataContext;
        listViewItem.IsSelected           = true;
    }

    private void LicenseText_OnMouseUp(object sender, MouseButtonEventArgs e) =>
        ViewModel.OpenLicense();

    private void VersionLabel_OnMouseUp(object sender, MouseButtonEventArgs e) =>
        ViewModel.OpenChangelog();

    private void SharedDeviceProfileButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OpenSharedDeviceProfile();

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        do
        {
            if (current is T ancestor)
                return ancestor;

            current = VisualTreeHelper.GetParent(current!);
        }
        while (current != null);

        return null;
    }
}
