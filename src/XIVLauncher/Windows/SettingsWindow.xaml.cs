using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game.Integrity;
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
        AddonListView.ContextMenu?.DataContext = viewModel;

        DiscordButton.Click += (_, _) => Process.Start(new ProcessStartInfo(Links.DISCORD_URL) { UseShellExecute = true });
    }

    private async void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ViewModel.SaveToSettingsAsync())
            return;

        var storyboard = new Storyboard();

        var fadeOut = new DoubleAnimation
        {
            From           = 1,
            To             = 0,
            Duration       = new Duration(TimeSpan.FromSeconds(0.15)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var slideDown = new DoubleAnimation
        {
            From           = 0,
            To             = 15,
            Duration       = new Duration(TimeSpan.FromSeconds(0.15)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        Storyboard.SetTargetProperty(fadeOut,   new PropertyPath("Opacity"));
        Storyboard.SetTargetProperty(slideDown, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

        storyboard.Children.Add(fadeOut);
        storyboard.Children.Add(slideDown);

        storyboard.Completed += (s, args) => Close();

        BeginStoryboard(storyboard);
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

        window.Owner         = Owner;
        window.ShowInTaskbar = false;

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

            current = VisualTreeHelper.GetParent(current!);
        }
        while (current != null);

        return null;
    }
}
