using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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

        DiscordButton.Click += SupportLinks.OpenDiscordChannel;
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

    private async Task ExecuteIntegrityCheckAsync(Func<IProgress<IntegrityCheck.IntegrityCheckProgress>, Task> action)
    {
        var window   = new IntegrityCheckProgressWindow();
        var progress = new Progress<IntegrityCheck.IntegrityCheckProgress>();

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
                PackGenerator.PackAndShowMessage();
                break;

            case MessageBoxResult.Cancel:
                return;
        }
#else
        if (_hasTriggeredLogo)
            return;

        Process.Start("explorer.exe", $"/select, \"{PackGenerator.SavePack()}\"");
        _hasTriggeredLogo = true;
#endif
    }
}
