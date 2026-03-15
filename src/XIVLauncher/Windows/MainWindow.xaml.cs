using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CheapLoc;
using MaterialDesignThemes.Wpf;
using Serilog;
using XIVLauncher.Accounts;
using XIVLauncher.Common;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Common.Http.Site;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel;
using XIVLauncher.Xaml;
using Timer = System.Timers.Timer;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindowViewModel Model => DataContext as MainWindowViewModel;

    private readonly AccountManager _accountManager;
    private readonly Launcher       _launcher;

    private const int                   CURRENT_VERSION_LEVEL = 2;
    private       Timer                 _bannerChangeTimer;
    private       Headlines             _headlines;
    private       IReadOnlyList<Banner> _banners;
    private       BitmapImage[]         _bannerBitmaps;
    private       int                   _currentBannerIndex;
    private       bool                  _everShown;

    private ObservableCollection<BannerDotInfo> _bannerDotList;

    public MainWindow()
    {
        InitializeComponent();

        DataContext     = new MainWindowViewModel(this);
        _accountManager = Model.AccountManager;
        _launcher       = Model.Launcher;

        Closed  += Model.OnWindowClosed;
        Closing += Model.OnWindowClosing;

        Model.LoginCardTransitionerIndex = 1;

        Model.Activate += () => Dispatcher.Invoke
        (() =>
            {
                Show();
                Activate();
                Focus();
            }
        );

        Model.Hide += () => Dispatcher.Invoke(() => { Hide(); });

        Model.ReloadHeadlines += () => Task.Run(SetupHeadlines);

        LoginTypeSelection.ItemsSource   = LoginTypeOption.Get(App.Settings.ShowWeGameTokenLogin.GetValueOrDefault(false));
        LoginTypeSelection.SelectedValue = App.Settings.SelectedLoginType.GetValueOrDefault(LoginType.Slide);
        NewsListView.ItemsSource = new List<News>
        {
            new()
            {
                Title = Loc.Localize("NewsLoading", "Loading..."),
                Tag   = "DlError"
            }
        };

#if !XL_NOAUTOUPDATE
        Title += " v" + AppUtil.GetAssemblyVersion();
#else
        Title += " v" + AppUtil.GetAssemblyVersion();
#endif

#if !XL_NOAUTOUPDATE
        if (EnvironmentSettings.IsDisableUpdates)
#endif
        {
        }

    }

    public void Initialize()
    {
        SetDefaults();

        Model.IsFastLogin = App.Settings.FastLogin;

        var savedAccount = _accountManager.CurrentAccount;

        if (App.GlobalIsDisableAutologin)
        {
            Log.Information("Autologin was disabled globally, saving into settings...");
            App.Settings.AutologinEnabled = false;
        }

        if (App.Settings.AutologinEnabled && savedAccount != null && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            Log.Information("Engaging Autologin...");

            if (savedAccount.AccountType == XivAccountType.WeGameSid)
            {
                Model.TryLogin
                (
                    LoginType.WeGameSID,
                    savedAccount.LoginAccount,
                    savedAccount.TestSID,
                    Model.IsFastLogin,
                    Model.IsReadWegameInfo,
                    LoginAfterAction.Start
                );
            }
            else
            {
                Model.TryLogin
                (
                    LoginType.AutoLoginSession,
                    savedAccount.LoginAccount,
                    savedAccount.AutoLoginSessionKey,
                    Model.IsFastLogin,
                    Model.IsReadWegameInfo,
                    LoginAfterAction.Start
                );
            }

            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || bool.Parse(Environment.GetEnvironmentVariable("XL_NOAUTOLOGIN") ?? "false"))
        {
            App.Settings.AutologinEnabled = false;
            //AutoLoginCheckBox.IsChecked = false;
        }

        if (App.Settings.GamePath?.Exists != true)
        {
            var setup = new FirstTimeSetup();
            setup.ShowDialog();

            // If the user didn't reach the end of the setup, we should quit
            if (!setup.WasCompleted)
            {
                Environment.Exit(0);
                return;
            }

            SettingsControl.ReloadSettings();
        }

        Task.Run
        (async () =>
            {
                SetupServers().Wait();
                Dispatcher.Invoke
                (() =>
                    {
                        if (savedAccount != null)
                            SwitchAccount(savedAccount, false);
                        ;
                    }
                );

                await SetupHeadlines();
                Troubleshooting.LogTroubleshooting();
                ;
            }
        );

        Log.Information("MainWindow initialized.");

        Show();
        Activate();

        _everShown = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        try
        {
            PreserveWindowPosition.RestorePosition(this);

            // Restore the size of the window to what we expect it to be
            // There's no better way to do it that doesn't make me wanna off myself
            Width  = 700;
            Height = 376;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Couldn't restore window position");
        }
    }

    private async Task SetupServers()
    {
        var areas = new LoginArea[1] { new() { AreaName = "获取大区失败", AreaID = "-1" } };
        areas = await LoginArea.Get();

        Dispatcher.Invoke
        (() =>
            {
                Model.LoginAreas = [.. areas];
                Model.Area     = Model.LoginAreas[0];
            }
        );
    }

    private async Task SetupHeadlines()
    {
        try
        {
            _bannerChangeTimer?.Stop();

            _headlines = await Headlines.GetHeadlinesAsync(_launcher)
                                        .ConfigureAwait(false);
            _banners = _headlines.Banner;

            _bannerBitmaps = new BitmapImage[_banners.Count];
            _bannerDotList = [];

            for (var i = 0; i < _banners.Count; i++)
            {
                var imageBytes = await _launcher.DownloadAsLauncher(_banners[i].LsbBanner.ToString());

                using var stream = new MemoryStream(imageBytes);

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = stream;
                bitmapImage.CacheOption  = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                _bannerBitmaps[i] = bitmapImage;
                _bannerDotList.Add(new() { Index = i });
            }

            _bannerDotList[0].Active = true;

            _ = Dispatcher.BeginInvoke
            (
                new Action
                (() =>
                    {
                        BannerImage.Source    = _bannerBitmaps[0];
                        BannerDot.ItemsSource = _bannerDotList;
                    }
                )
            );

            _bannerChangeTimer = new Timer { Interval = 5000 };

            _bannerChangeTimer.Elapsed += (o, args) =>
            {
                _bannerDotList.ToList().ForEach(x => x.Active = false);

                if (_currentBannerIndex + 1 > _banners.Count - 1)
                    _currentBannerIndex = 0;
                else
                    _currentBannerIndex++;

                _bannerDotList[_currentBannerIndex].Active = true;

                Dispatcher.BeginInvoke
                (
                    new Action
                    (() =>
                        {
                            BannerImage.Source    = _bannerBitmaps[_currentBannerIndex];
                            BannerDot.ItemsSource = _bannerDotList.ToList();
                        }
                    )
                );
            };

            _bannerChangeTimer.AutoReset = true;
            _bannerChangeTimer.Start();

            _ = Dispatcher.BeginInvoke(new Action(() => { NewsListView.ItemsSource = _headlines.News; }));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not get news");
            _ = Dispatcher.BeginInvoke
                (new Action(() => { NewsListView.ItemsSource = new List<News> { new() { Title = Loc.Localize("NewsDlFailed", "Could not download news data."), Tag = "DlError" } }; }));
        }
    }

    private void SetDefaults()
    {
        // Set the default patch acquisition method
        App.Settings.PatchAcquisitionMethod ??= AcquisitionMethod.Aria;

        // Set the default Dalamud injection method
        App.Settings.InGameAddonLoadMethod ??= DalamudLoadMethod.EntryPoint;

        // Clean up invalid addons
        if (App.Settings.AddonList != null)
            App.Settings.AddonList = App.Settings.AddonList.Where(x => !string.IsNullOrEmpty(x.Addon.Path)).ToList();

        App.Settings.AskBeforePatchInstall ??= true;

        App.Settings.DpiAwareness ??= DpiAwareness.Unaware;

        App.Settings.TreatNonZeroExitCodeAsFailure ??= false;
        App.Settings.ExitLauncherAfterGameExit     ??= true;

        var versionLevel = App.Settings.VersionUpgradeLevel.GetValueOrDefault(0);

        while (versionLevel < CURRENT_VERSION_LEVEL)
        {
            switch (versionLevel)
            {
                case 0:
                    // Check for RTSS & Special K injectors
                    try
                    {
                        var hasRtss = Process.GetProcesses().Any
                        (x =>
                             x.ProcessName.ToLowerInvariant().Contains("rtss") || x.ProcessName.ToLowerInvariant().Contains("skifsvc64")
                        );

                        if (hasRtss)
                        {
                            App.Settings.DalamudInjectionDelayMs = 4000;
                            Log.Information("RTSS/SpecialK detected, setting delay");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Could not check for RTSS/SpecialK");
                    }

                    break;

                // 5.12.2022: Bad main window placement when using auto-launch
                case 1:
                    App.Settings.MainWindowPlacement = null;
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            versionLevel++;
        }

        App.Settings.VersionUpgradeLevel = versionLevel;
    }

    private void BannerCard_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (_headlines != null) Process.Start(new ProcessStartInfo(_banners[_currentBannerIndex].Link.ToString()) { UseShellExecute = true });
    }

    private void NewsListView_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (_headlines == null)
            return;

        if (!(NewsListView.SelectedItem is News item))
            return;

        if (!string.IsNullOrEmpty(item.Url))
            Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true });
        else if (!string.IsNullOrEmpty(item.Id))
            Process.Start(new ProcessStartInfo($"https://ff.web.sdo.com/web8/index.html#/newstab/newscont/{item.Id}") { UseShellExecute = true });
        //else
        //{
        //    string url;

        //    switch (App.Settings.Language)
        //    {
        //        case ClientLanguage.Japanese:
        //            url = "https://jp.finalfantasyxiv.com/lodestone/news/detail/";
        //            break;

        //        case ClientLanguage.English when GameHelpers.IsRegionNorthAmerica():
        //            url = "https://na.finalfantasyxiv.com/lodestone/news/detail/";
        //            break;

        //        case ClientLanguage.English:
        //            url = "https://eu.finalfantasyxiv.com/lodestone/news/detail/";
        //            break;

        //        case ClientLanguage.German:
        //            url = "https://de.finalfantasyxiv.com/lodestone/news/detail/";
        //            break;

        //        case ClientLanguage.French:
        //            url = "https://fr.finalfantasyxiv.com/lodestone/news/detail/";
        //            break;

        //        case ClientLanguage.ChineseSimplified:
        //            url = "https://na.finalfantasyxiv.com/lodestone/news/detail/";
        //            break;

        //        default:
        //            url = "https://eu.finalfantasyxiv.com/lodestone/news/detail/";
        //            break;
        //    }

        //    Process.Start(url + item.Id);
        //}
    }

    private void WorldStatusButton_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://ff.web.sdo.com/web8/index.html#/servers") { UseShellExecute = true });

    private void QuitMaintenanceQueueButton_OnClick(object sender, RoutedEventArgs e) =>
        Model.IsLoadingDialogOpen = false;

    private void Card_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return)
            return;

        if (Model.IsLoggingIn)
            return;

        Model.StartLoginCommand.Execute(null);
    }

    private void AccountSwitcherButton_OnClick(object sender, RoutedEventArgs e)
    {
        var switcher = new AccountSwitcher(_accountManager);

        var locationFromScreen = AccountSwitcherButton.PointToScreen(new Point(0, 0));
        var source             = PresentationSource.FromVisual(this);

        if (source != null)
        {
            var targetPoints = source.CompositionTarget!.TransformFromDevice.Transform(locationFromScreen);

            switcher.WindowStartupLocation = WindowStartupLocation.Manual;
            switcher.Left                  = targetPoints.X - 15;
            switcher.Top                   = targetPoints.Y - 15;
        }

        switcher.OnAccountSwitchedEventHandler += OnAccountSwitchedEventHandler;

        switcher.Show();
    }

    private void OnAccountSwitchedEventHandler(object sender, XivAccount e) =>
        SwitchAccount(e, true);

    private void SwitchAccount(XivAccount account, bool saveAsCurrent)
    {
        if (saveAsCurrent)
            _accountManager.CurrentAccount = account;

        Model.Username = account.UserName;
        //Model.IsOtp = account.UseOtp;
        Model.IsFastLogin      = account.AutoLogin;
        Model.Area             = Model.LoginAreas.Where(x => x.AreaName == account.AreaName).FirstOrDefault();
        LoginPassword.Password = string.Empty;

        switch (account.AccountType)
        {
            case XivAccountType.Sdo:
                if (account.Password is not null)
                {
                    LoginTypeSelection.SelectedValue = LoginType.Static;

                    // Make users happy by not showing their password
                    LoginPassword.Password = MainWindowViewModel.PRESUDO_PASSWORD;
                }
                else
                    LoginTypeSelection.SelectedValue = LoginType.Slide;

                break;

            case XivAccountType.WeGame:
                LoginTypeSelection.SelectedValue = LoginType.WeGameToken;
                LoginPassword.Password           = MainWindowViewModel.PRESUDO_PASSWORD;
                break;

            case XivAccountType.WeGameSid:
                LoginTypeSelection.SelectedValue = LoginType.WeGameSID;
                break;
        }
    }

    private void SettingsControl_OnSettingsDismissed(object sender, EventArgs e) =>
        Task.Run(SetupHeadlines);
    
    private void LoginPassword_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext != null)
            ((MainWindowViewModel)DataContext).Password = ((PasswordBox)sender).Password;
    }

    private void RadioButton_MouseEnter(object sender, MouseEventArgs e)
    {
        ((RadioButton)sender).IsChecked = true;
        _currentBannerIndex             = _bannerDotList.FirstOrDefault(x => x.Active)?.Index ?? _currentBannerIndex;
        Dispatcher.BeginInvoke(new Action(() => BannerImage.Source = _bannerBitmaps[_currentBannerIndex]));

        _bannerChangeTimer.Stop();
    }

    private void RadioButton_MouseLeave(object sender, MouseEventArgs e) =>
        _bannerChangeTimer.Start();

    private void SettingsControl_OnCloseMainWindowGracefully(object sender, EventArgs e) =>
        Close();

    private void MainWindow_OnClosing(object sender, CancelEventArgs e)
    {
        if (!_everShown)
            return;

        try
        {
            PreserveWindowPosition.SaveWindowPosition(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Couldn't save window position");
        }
    }

    private void LoginTypeSelection_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedItem = (LoginTypeOption)((ComboBox)sender).SelectedItem;
        if (DataContext != null)
            ((MainWindowViewModel)DataContext).LoginTypeOption = selectedItem;
        App.Settings.SelectedLoginType = selectedItem.LoginType;
        // Default
        LoginUsername.Visibility = Visibility.Visible;
        LoginPassword.Visibility = Visibility.Collapsed;

        FastLoginCheckBox.Visibility      = Visibility.Visible;
        ReadWeGameInfoCheckBox.Visibility = Visibility.Collapsed;
        FastLoginCheckBox.Content         = "快速登录";
        LoginPassword.Password            = string.Empty;
        HintAssist.SetHint(LoginUsername, "盛趣账号");
        HintAssist.SetHint(LoginPassword, "密码");

        switch (selectedItem.LoginType)
        {
            //Todo: 各种地方的Hint
            case LoginType.Slide:
                break;

            case LoginType.QRCode:
                LoginUsername.Visibility = Visibility.Hidden;
                break;

            case LoginType.Static:
                LoginUsername.Visibility  = Visibility.Visible;
                LoginPassword.Visibility  = Visibility.Visible;
                FastLoginCheckBox.Content = "保存密码";
                //FastLoginCheckBox.Visibility = Visibility.Collapsed;
                break;

            case LoginType.WeGameToken:
                LoginPassword.Visibility = Visibility.Visible;
                HintAssist.SetHint(LoginUsername, "SndaId");
                HintAssist.SetHint(LoginPassword, "抓包Token");
                break;

            case LoginType.WeGameSID:
                FastLoginCheckBox.Visibility      = Visibility.Collapsed;
                ReadWeGameInfoCheckBox.Visibility = Visibility.Visible;
                HintAssist.SetHint(LoginUsername, "从Wegame自动获取的账号");
                break;
        }
    }

    private void LoginUsername_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext != null)
            ((MainWindowViewModel)DataContext).Username = ((TextBox)sender).Text;
    }

    private void FastLoginCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        //if (Model.IsFastLogin)
        //{
        //    LoginPassword.Password = String.Empty;
        //}
        //else
        //{
        //    LoginPassword.Password = _accountManager.CurrentAccount?.Password;
        //}
    }

    //private void InjectGame_OnClick(object sender, RoutedEventArgs e)
    //{
    //    Task.Run(() =>
    //    {
    //        Model.InjectGame();
    //        Environment.Exit(0);
    //    }).ConfigureAwait(false);

    //}

    private void BackToLoginPageButton_OnClick(object sender, RoutedEventArgs e) =>
        Dispatcher.Invoke(() => { Model.SwitchCard(MainWindowViewModel.LoginCardType.MainPage); });

    private void InjectButton_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.Invoke
        (() =>
            {
                if (Model.SelectedProcess != null)
                    AppUtil.BringProcessMainWindowToFront(Model.SelectedProcess.ProcessID);
            }
        );
    }

    //private SdoArea[] _sdoAreas;
    private class BannerDotInfo
    {
        public bool Active { get; set; }
        public int  Index  { get; set; }
    }
}
