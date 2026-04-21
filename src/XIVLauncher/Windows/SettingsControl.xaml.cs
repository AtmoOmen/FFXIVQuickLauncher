using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MaterialDesignThemes.Wpf.Transitions;
using XIVLauncher.Common.Game;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for SettingsControl.xaml
/// </summary>
public partial class SettingsControl
{
    private SettingsControlViewModel ViewModel => (SettingsControlViewModel)DataContext;
    private bool                     _hasTriggeredLogo;

    public SettingsControl()
    {
        InitializeComponent();

        AddonListView.ContextMenu.DataContext = DataContext;
        DiscordButton.Click += SupportLinks.OpenDiscordChannel;
    }

    private void SettingsControl_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (AddonListView.ContextMenu != null)
            AddonListView.ContextMenu.DataContext = e.NewValue;
    }

    public void ReloadSettings() =>
        ViewModel.ReloadFromSettings();

    private async void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ViewModel.SaveToSettingsAsync())
            return;

        Transitioner.MoveNextCommand.Execute(null, null);
    }

    private void AddonListView_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        ViewModel.EditSelectedAddon();
    }

    private void AddonListView_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource) is not { } listViewItem)
            return;

        AddonListView.SelectedItem = listViewItem.DataContext;
        listViewItem.IsSelected    = true;
    }

    private async void RunIntegrityCheck_OnClick(object sender, RoutedEventArgs e) =>
        await ExecuteIntegrityCheckAsync
        (async progress =>
            {
                var result = await ViewModel.RunIntegrityCheckAsync(progress);
                if (result != null)
                    ViewModel.ShowIntegrityCheckResult(result);
            }
        );

    private async void GenerateIntegrityCheck_OnClick(object sender, RoutedEventArgs e) =>
        await ExecuteIntegrityCheckAsync
        (async progress =>
            {
                var outputPath = await ViewModel.GenerateIntegrityCheckAsync(progress);
                if (!string.IsNullOrWhiteSpace(outputPath))
                    ViewModel.ShowGeneratedIntegrityCheckResult(outputPath);
            }
        );

    private async Task ExecuteIntegrityCheckAsync(Func<IProgress<IntegrityCheckProgress>, Task> action)
    {
        var window   = new IntegrityCheckProgressWindow();
        var progress = new Progress<IntegrityCheckProgress>();

        progress.ProgressChanged += (_, checkProgress) => window.UpdateProgress(checkProgress);

        var owner = Window.GetWindow(this);

        if (owner != null)
        {
            window.Owner         = owner;
            window.ShowInTaskbar = false;
        }

        try
        {
            window.Show();
            await action(progress);
        }
        finally
        {
            window.Close();
        }
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

            current = VisualTreeHelper.GetParent(current);
        }
        while (current != null);

        return null;
    }

    private void Logo_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
#if DEBUG
        var result = MessageBox.Show
        (
            "是：首次设置\n否：保存疑难排查包\n取消：返回",
            "XIVLauncher 调试入口",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question
        );

        switch (result)
        {
            case MessageBoxResult.Yes:
            {
                var setup = new FirstTimeSetup();
                setup.ShowDialog();
                ReloadSettings();
                break;
            }

            case MessageBoxResult.No:
                PackGenerator.PackAndShowMessage(Window.GetWindow(this));
                break;

            case MessageBoxResult.Cancel:
                return;
        }
#else
        if (_hasTriggeredLogo)
            return;

        PackGenerator.OpenPackLocation(PackGenerator.SavePack());
        _hasTriggeredLogo = true;
#endif
    }
}
