using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaterialDesignThemes.Wpf.Transitions;
using Serilog;
using XIVLauncher.Accounts.Cred;
using XIVLauncher.Common;
using XIVLauncher.Common.Addon;
using XIVLauncher.Common.Addon.Implementations;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Common.Util;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for SettingsControl.xaml
/// </summary>
public partial class SettingsControl
{
    private SettingsControlViewModel ViewModel => DataContext as SettingsControlViewModel;

    private const int BYTES_TO_MB = 1048576;

    private bool _hasTriggeredLogo = false;

    private MainWindowViewModel MainWindowViewModel;

    public SettingsControl()
    {
        InitializeComponent();

        DiscordButton.Click += SupportLinks.OpenDiscordChannel;
        DataContext         =  new SettingsControlViewModel();
        ReloadSettings();
    }

    public void ReloadSettings()
    {
        if (App.Settings.GamePath != null)
            ViewModel.GamePath = App.Settings.GamePath.FullName;

        if (App.Settings.PatchPath is { Exists: false })
            App.Settings.PatchPath = null;

        App.Settings.PatchPath ??= new DirectoryInfo(Path.Combine(Paths.RoamingPath, "patches"));

        if (App.Settings.PatchPath != null)
            ViewModel.PatchPath = App.Settings.PatchPath.FullName;

        ViewModel.LauncherLanguage               = LauncherLanguage.SimplifiedChinese;
        ViewModel.LauncherLanguageNoticeVisiable = false;
        AddonListView.ItemsSource                = App.Settings.AddonList ??= [];
        AskBeforePatchingCheckBox.IsChecked      = App.Settings.AskBeforePatchInstall;
        KeepPatchesCheckBox.IsChecked            = App.Settings.KeepPatches;
        PatchAcquisitionComboBox.SelectedIndex   = (int)App.Settings.PatchAcquisitionMethod.GetValueOrDefault(AcquisitionMethod.Aria);

        InjectionDelayUpDown.Value = App.Settings.DalamudInjectionDelayMs;

        if (App.Settings.InGameAddonLoadMethod == DalamudLoadMethod.DllInject)
            DllInjectDalamudLoadMethodRadioButton.IsChecked = true;
        else
            EntryPointDalamudLoadMethodRadioButton.IsChecked = true;

        EnableHooksCheckBox.IsChecked = App.Settings.InGameAddonEnabled;

        EnableDcTravelCheckBox.IsChecked = true;
        EnableDcTravelCheckBox.IsEnabled = false;

        LaunchArgsTextBox.Text = App.Settings.AdditionalLaunchArgs;

        DpiAwarenessComboBox.SelectedIndex = (int)App.Settings.DpiAwareness.GetValueOrDefault(DpiAwareness.Unaware);

        VersionLabel.Text += " - v" + AppUtil.GetAssemblyVersion() + " - " + AppUtil.GetGitHash() + " - " + Environment.Version;

        var val = (decimal)App.Settings.SpeedLimitBytes / BYTES_TO_MB;

        SpeedLimiterUpDown.Value = val;

        DynamicDeviceIdCheckBox.IsChecked = App.Settings.DynamicDeviceId;

        AccountStorageEncryptCombox.SelectedIndex = (int)App.Settings.CredType.GetValueOrDefault(CredType.WindowsCredManager);
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.GamePath == ViewModel.PatchPath)
        {
            CustomMessageBox.Show
            (
                "游戏和补丁的下载路径不能相同\n请选择不同的游戏和补丁下载路径",
                "XIVLauncher Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: Window.GetWindow(this)
            );
            return;
        }

        App.Settings.GamePath  = !string.IsNullOrEmpty(ViewModel.GamePath) ? new DirectoryInfo(ViewModel.GamePath) : null;
        App.Settings.PatchPath = !string.IsNullOrEmpty(ViewModel.PatchPath) ? new DirectoryInfo(ViewModel.PatchPath) : null;

        // Keep the notice visible if LauncherLanguage has changed
        App.Settings.LauncherLanguage = LauncherLanguage.SimplifiedChinese;

        App.Settings.AddonList              = (List<AddonEntry>)AddonListView.ItemsSource;
        App.Settings.AskBeforePatchInstall  = AskBeforePatchingCheckBox.IsChecked == true;
        App.Settings.KeepPatches            = KeepPatchesCheckBox.IsChecked       == true;
        App.Settings.PatchAcquisitionMethod = (AcquisitionMethod)PatchAcquisitionComboBox.SelectedIndex;

        App.Settings.InGameAddonEnabled = EnableHooksCheckBox.IsChecked == true;

        if (InjectionDelayUpDown.Value.HasValue)
            App.Settings.DalamudInjectionDelayMs = InjectionDelayUpDown.Value.Value;

        if (DllInjectDalamudLoadMethodRadioButton.IsChecked == true)
            App.Settings.InGameAddonLoadMethod = DalamudLoadMethod.DllInject;
        else
            App.Settings.InGameAddonLoadMethod = DalamudLoadMethod.EntryPoint;

        App.Settings.AdditionalLaunchArgs = LaunchArgsTextBox.Text;

        App.Settings.DpiAwareness = (DpiAwareness)DpiAwarenessComboBox.SelectedIndex;

        SettingsDismissed?.Invoke(this, null);

        App.Settings.SpeedLimitBytes = (long)(SpeedLimiterUpDown.Value * BYTES_TO_MB);
        App.Settings.GitHubToken     = ViewModel.GitHubToken;

        App.Settings.DynamicDeviceId = DynamicDeviceIdCheckBox.IsChecked == true;
        // Apply setting immediately
        MachineCode.IsDynamicDeviceId = App.Settings.DynamicDeviceId;

        App.Settings.CredType = (CredType)AccountStorageEncryptCombox.SelectedIndex;
        App.AccountManager.ChangeCredType(App.Settings.CredType);

        Transitioner.MoveNextCommand.Execute(null, null);
    }

    private void GitHubButton_OnClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/AtmoOmen/FFXIVQuickLauncher") { UseShellExecute = true });

    private void BackupToolButton_OnClick(object sender, RoutedEventArgs e) =>
        Process.Start(Path.Combine(ViewModel.GamePath, "boot", "ffxivconfig64.exe"));

    private void OriginalLauncherButton_OnClick(object sender, RoutedEventArgs e) =>
        GameHelpers.StartOfficialLauncher(App.Settings.GamePath);

    // All of the list handling is very dirty - but i guess it works

    private void AddAddon_OnClick(object sender, RoutedEventArgs e)
    {
        var addonSetup = new GenericAddonSetupWindow();
        addonSetup.ShowDialog();

        if (addonSetup.Result != null && !string.IsNullOrEmpty(addonSetup.Result.Path))
        {
            var addonList = App.Settings.AddonList;

            addonList.Add
            (
                new AddonEntry
                {
                    IsEnabled = true,
                    Addon     = addonSetup.Result
                }
            );

            App.Settings.AddonList = addonList;

            AddonListView.ItemsSource = App.Settings.AddonList;
        }
    }

    private void AddonListView_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (!(AddonListView.SelectedItem is AddonEntry entry))
            return;

        if (entry.Addon is GenericAddon genericAddon)
        {
            var selectedIndex = AddonListView.SelectedIndex;
            var addonSetup    = new GenericAddonSetupWindow(genericAddon);
            addonSetup.ShowDialog();

            if (addonSetup.Result != null)
            {
                var addonList = App.Settings.AddonList;
                addonList.RemoveAt(selectedIndex);
                addonList.Insert
                (
                    selectedIndex,
                    new AddonEntry
                    {
                        IsEnabled = entry.IsEnabled,
                        Addon     = addonSetup.Result
                    }
                );

                App.Settings.AddonList = addonList;

                AddonListView.ItemsSource = App.Settings.AddonList;
            }
        }
    }

    private void ToggleButton_OnChecked(object sender, RoutedEventArgs e) =>
        App.Settings.AddonList = (List<AddonEntry>)AddonListView.ItemsSource;

    private void RemoveAddonEntry_OnClick(object sender, RoutedEventArgs e)
    {
        if (AddonListView.SelectedItem is AddonEntry)
        {
            var addonList = App.Settings.AddonList;
            addonList.RemoveAt(AddonListView.SelectedIndex);

            App.Settings.AddonList = addonList;

            AddonListView.ItemsSource = App.Settings.AddonList;
        }
    }

    private void RunIntegrityCheck_OnClick(object s, RoutedEventArgs e)
    {
        var window   = new IntegrityCheckProgressWindow();
        var progress = new Progress<IntegrityCheck.IntegrityCheckProgress>();
        progress.ProgressChanged += (sender, checkProgress) => window.UpdateProgress(checkProgress);

        var gamePath = new DirectoryInfo(ViewModel.GamePath);

        if (Repository.Ffxiv.IsBaseVer(gamePath))
        {
            CustomMessageBox.Show
            (
                "游戏没有安装到指定的路径\n请在检查游戏完整性之前安装游戏",
                "XIVLauncherCN (Soil)",
                parentWindow: Window.GetWindow(this)
            );
            return;
        }

        Task.Run(async () => await IntegrityCheck.CompareIntegrityAsync(progress, gamePath)).ContinueWith
        (task =>
            {
                window.Dispatcher.Invoke(() => window.Close());

                var saveIntegrityPath = Path.Combine(Paths.RoamingPath, "integrityreport.txt");
#if DEBUG
                Log.Information("Saving integrity to " + saveIntegrityPath);
#endif
                File.WriteAllText(saveIntegrityPath, task.Result.report);

                Dispatcher.Invoke
                (() =>
                    {
                        switch (task.Result.compareResult)
                        {
                            case IntegrityCheck.CompareResult.ReferenceNotFound:
                                CustomMessageBox.Show
                                (
                                    "此游戏版本尚无参考报告, 请稍后重试",
                                    "XIVLauncherCN (Soil)",
                                    parentWindow: Window.GetWindow(this)
                                );
                                return;

                            case IntegrityCheck.CompareResult.ReferenceFetchFailure:
                                CustomMessageBox.Show
                                (
                                    "下载完整性检查参考文件失败, 请检查网络连接后重试",
                                    "XIVLauncherCN (Soil)",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error,
                                    parentWindow: Window.GetWindow(this)
                                );
                                return;

                            case IntegrityCheck.CompareResult.Invalid:
                                CustomMessageBox.Show
                                (
                                    "部分游戏文件似乎已被修改或损坏\n\n如果使用 TexTools 模组, 这是预期结果\n\n如果不使用模组, 请右键点击 XIVLauncher 启动页面的「登录」按钮并选择「修复游戏」",
                                    "XIVLauncherCN (Soil)",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Exclamation,
                                    showReportLinks: true,
                                    parentWindow: Window.GetWindow(this)
                                );
                                break;

                            case IntegrityCheck.CompareResult.Valid:
                                CustomMessageBox.Show("游戏安装完整", "XIVLauncherCN (Soil)", parentWindow: Window.GetWindow(this));
                                break;
                        }
                    }
                );
            }
        );

        window.ShowDialog();
    }

    private void GenerateIntegrityCheck_OnClick(object sender, RoutedEventArgs e)
    {
        var window   = new IntegrityCheckProgressWindow();
        var progress = new Progress<IntegrityCheck.IntegrityCheckProgress>();
        progress.ProgressChanged += (sender, checkProgress) => window.UpdateProgress(checkProgress);

        var gamePath = new DirectoryInfo(ViewModel.GamePath);

        if (Repository.Ffxiv.IsBaseVer(gamePath))
        {
            CustomMessageBox.Show
            (
                "游戏没有安装到指定的路径\n请在检查游戏完整性之前安装游戏",
                "XIVLauncherCN (Soil)",
                parentWindow: Window.GetWindow(this)
            );
            return;
        }

        Task.Run(async () => await IntegrityCheck.GenerateIntegrityAsync(progress, gamePath)).ContinueWith
        (task =>
            {
                window.Dispatcher.Invoke(() => window.Close());

                Dispatcher.Invoke(() => { CustomMessageBox.Show("已完成游戏客户端哈希数据生成, 相关文件保存在:\n" + $"{task.Result}", "XIVLauncherCN (Soil)", parentWindow: Window.GetWindow(this)); });
            }
        );

        window.ShowDialog();
    }
    
    private void PluginsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var pluginsPath = Path.Combine(Paths.RoamingPath, "installedPlugins");

        try
        {
            Directory.CreateDirectory(pluginsPath);
            Process.Start(new ProcessStartInfo(pluginsPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            var error = $"Could not open the plugins folder! {pluginsPath}";
            CustomMessageBox.Show
            (
                error,
                "XIVLauncher Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: Window.GetWindow(this)
            );
            Log.Error(ex, error);
        }
    }

    private void GamePathEntry_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var isBootOrGame                   = false;
        var mightBeNonInternationalVersion = false;

        try
        {
            isBootOrGame                   = !GameHelpers.LetChoosePath(ViewModel.GamePath);
            mightBeNonInternationalVersion = GameHelpers.CanMightNotBeInternationalClient(ViewModel.GamePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not check game path");
        }

        if (isBootOrGame)
        {
            GamePathSafeguardText.Text       = "请不要选择「game」文件夹, 请选择它的上层文件夹";
            GamePathSafeguardText.Visibility = Visibility.Visible;
        }
        else if (mightBeNonInternationalVersion && App.Settings.Language != ClientLanguage.ChineseSimplified)
        {
            GamePathSafeguardText.Text       = "XIVLauncher 并不支持国服或韩服版本, 请确保选择的路径属于国际服版本";
            GamePathSafeguardText.Visibility = Visibility.Visible;
        }
        else
            GamePathSafeguardText.Visibility = Visibility.Collapsed;
    }

    private void LicenseText_OnMouseUp(object sender, MouseButtonEventArgs e) =>
        Process.Start(new ProcessStartInfo(Path.Combine(Paths.ResourcesPath, "LICENSE.txt")) { UseShellExecute = true });

    private void Logo_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
#if DEBUG
        var result = MessageBox.Show("Yes: FTS\nNo: Save troubleshooting\nCancel: Cancel", "XIVLauncher Expert Debugging Interface", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        switch (result)
        {
            case MessageBoxResult.Yes:
                var fts = new FirstTimeSetup();
                fts.ShowDialog();

                Log.Debug($"WasCompleted: {fts.WasCompleted}");

                ReloadSettings();
                break;

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

    private void VersionLabel_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var cw = new ChangelogWindow();
        cw.UpdateVersion(AppUtil.GetAssemblyVersion());
    }

    private void OpenAdvancedSettings_OnClick(object sender, RoutedEventArgs e)
    {
        var asw = new AdvancedSettingsWindow();
        asw.ShowDialog();
    }

    public event EventHandler SettingsDismissed;
    public event EventHandler CloseMainWindowGracefully;
}
