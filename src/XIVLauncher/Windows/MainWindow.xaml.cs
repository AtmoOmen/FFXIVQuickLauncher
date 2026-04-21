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
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;
using Serilog;
using XIVLauncher.Accounts;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Common.Http.Site;
using XIVLauncher.Common.Util;
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
    public MainWindowViewModel Model => (DataContext as MainWindowViewModel)!;

    private const int           CURRENT_VERSION_LEVEL = 2;
    
    private readonly AccountManager _accountManager;
    private readonly Launcher       _launcher;

    private Timer         _bannerChangeTimer;
    private Headlines     _headlines;
    private Banner[]      _banners;
    private BitmapImage[] _bannerBitmaps;
    private int           _currentBannerIndex;
    private bool          _everShown;
    private bool          _suppressAccountSelectionTracking;

    private ObservableCollection<BannerDotInfo> _bannerDotList;

    public MainWindow()
    {
        InitializeComponent();

        DataContext                  =  new MainWindowViewModel(this);
        _accountManager              =  Model.AccountManager;
        _launcher                    =  Model.Launcher;
        Model.Settings.SettingsSaved += (_, _) => Task.Run(SetupHeadlines);

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

        Model.Hide += () => Dispatcher.Invoke(Hide);

        Model.ReloadHeadlines += () => Task.Run(SetupHeadlines);

        NewsListView.ItemsSource = new List<News>
        {
            new()
            {
                Title = "加载中…",
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
        {
        }
#endif
    }

    public void Initialize()
    {
        SetDefaults();

        Model.LoginPage.IsFastLogin = App.Settings.FastLogin;

        var savedAccount = _accountManager.CurrentAccount;
        var hasUnavailableSecrets = _accountManager.HasUnavailableSecrets(savedAccount);

        if (App.GlobalIsDisableAutologin)
        {
            Log.Information("Autologin was disabled globally, saving into settings...");
            App.Settings.AutologinEnabled = false;
        }

        if (hasUnavailableSecrets && App.Settings.AutologinEnabled)
        {
            Log.Information("当前账号存在本会话不可读的旧密文，已禁用自动登录");
            App.Settings.AutologinEnabled = false;
        }

        if (App.Settings.AutologinEnabled && savedAccount != null && !hasUnavailableSecrets && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            Log.Information("自动登录中...");

            if (savedAccount.AccountType == XIVAccountType.WeGameSID)
            {
                Model.StartLogin
                (
                    LoginType.WeGameSID,
                    savedAccount.UserName,
                    string.Empty,
                    Model.LoginPage.IsFastLogin,
                    false,
                    LoginAfterAction.Start
                );
            }
            else
            {
                Model.StartLogin
                (
                    LoginType.AutoLoginSession,
                    savedAccount.LoginAccount,
                    savedAccount.AutoLoginSessionKey,
                    Model.LoginPage.IsFastLogin,
                    Model.LoginPage.IsReadWegameInfo,
                    LoginAfterAction.Start
                );
            }

            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || bool.Parse(Environment.GetEnvironmentVariable("XL_NOAUTOLOGIN") ?? "false"))
            App.Settings.AutologinEnabled = false;

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

        ShowCredTypeRecoveryMessage();

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
            Height = 420;
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
                Model.LoginPage.LoginAreas = [.. areas];
                Model.LoginPage.Area       = Model.LoginPage.LoginAreas[0];
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

            _bannerBitmaps = new BitmapImage[_banners.Length];
            _bannerDotList = [];

            for (var i = 0; i < _banners.Length; i++)
            {
                var imageBytes = await _launcher.DownloadAsLauncher(_banners[i].LsbBanner.ToString());

                using var stream = new MemoryStream(imageBytes);

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = stream;
                bitmapImage.CacheOption  = BitmapCacheOption.OnLoad;
                bitmapImage.DecodePixelWidth = 400;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                _bannerBitmaps[i] = bitmapImage;
                _bannerDotList.Add(new() { Index = i });
            }

            _currentBannerIndex = 0;
            SetBannerDotActiveState(_currentBannerIndex);

            _ = Dispatcher.BeginInvoke
            (
                new Action
                (() =>
                    {
                        BannerImage.Source    = _bannerBitmaps[_currentBannerIndex];
                        BannerDot.ItemsSource = _bannerDotList;
                    }
                )
            );

            _bannerChangeTimer = new Timer { Interval = 5000 };

            _bannerChangeTimer.Elapsed += (_, _) => Dispatcher.BeginInvoke(new Action(ShowNextBanner), DispatcherPriority.Background);

            _bannerChangeTimer.AutoReset = true;
            _bannerChangeTimer.Start();

            _ = Dispatcher.BeginInvoke(new Action(() => { NewsListView.ItemsSource = _headlines.News?.OrderByDescending(n => n.Date).ToList(); }));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not get news");
            _ = Dispatcher.BeginInvoke
                (new Action(() => { NewsListView.ItemsSource = new List<News> { new() { Title = "无法获取公告信息", Tag = "DlError" } }; }));
        }
    }

    private static void SetDefaults()
    {
        // Set the default patch acquisition method
        App.Settings.PatchAcquisitionMethod ??= AcquisitionMethod.Aria;

        // Set the default Dalamud injection method
        App.Settings.InGameAddonLoadMethod ??= DalamudLoadMethod.EntryPoint;

        // Clean up invalid addons
        if (App.Settings.AddonList != null)
            App.Settings.AddonList = App.Settings.AddonList.Where(x => !string.IsNullOrEmpty(x.Addon.Path)).ToList();

        App.Settings.AskBeforePatchInstall ??= true;
        App.Settings.RequireDeviceProfileSetupForNewAccountLogin ??= false;

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

        if (NewsListView.SelectedItem is not News item)
            return;

        if (!string.IsNullOrEmpty(item.Url))
            Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true });
        else if (!string.IsNullOrEmpty(item.Id))
            Process.Start(new ProcessStartInfo(Links.SDO_NEWS_ARTICLE_BASE_URL + item.Id) { UseShellExecute = true });
        
    }
    
    private void Card_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return)
            return;

        if (Model.IsLoggingIn)
            return;

        Model.LoginPage.StartLoginCommand.Execute(null);
    }

    private void AccountSwitcherButton_OnClick(object sender, RoutedEventArgs e)
    {
        var switcher = new AccountSwitcher(_accountManager, this)
        {
            ShowInTaskbar = false
        };

        var locationFromScreen = AccountSwitcherButton.PointToScreen(new Point(0, 0));
        var source             = PresentationSource.FromVisual(this);

        if (source != null)
        {
            var targetPoints = source.CompositionTarget!.TransformFromDevice.Transform(locationFromScreen);

            switcher.WindowStartupLocation = WindowStartupLocation.Manual;
            switcher.Left                  = targetPoints.X - 15;
            switcher.Top                   = targetPoints.Y - 15;
        }

        switcher.AccountSwitched += OnAccountSwitchedEventHandler;

        switcher.Show();
    }

    private void OnAccountSwitchedEventHandler(object? sender, XIVAccount e) =>
        SwitchAccount(e, true);

    private void SwitchAccount(XIVAccount account, bool saveAsCurrent)
    {
        _suppressAccountSelectionTracking = true;

        try
        {
            if (saveAsCurrent)
                _accountManager.CurrentAccount = account;

            var hasUnavailableSecrets = _accountManager.HasUnavailableSecrets(account);
            var currentLoginType      = Model.LoginPage.LoginTypeOption.LoginType;

            Model.LoginPage.IsFastLogin = account.AutoLogin;
            Model.LoginPage.Area        = Model.LoginPage.LoginAreas.Where(x => x.AreaName == account.AreaName).FirstOrDefault();
            LoginPassword.Password      = string.Empty;

            switch (account.AccountType)
            {
                case XIVAccountType.Sdo:
                    var nextLoginType = currentLoginType is LoginType.Static or LoginType.Slide
                        ? currentLoginType
                        : string.IsNullOrWhiteSpace(account.Password)
                            ? LoginType.Slide
                            : LoginType.Static;

                    Model.LoginPage.SelectLoginType(nextLoginType);

                    if (nextLoginType == LoginType.Static && !string.IsNullOrWhiteSpace(account.Password))
                    {
                        if (!hasUnavailableSecrets)
                        {
                            // Make users happy by not showing their password
                            LoginPassword.Password = MainWindowViewModel.PRESUDO_PASSWORD;
                        }
                    }

                    break;

                case XIVAccountType.WeGame:
                    if (currentLoginType != LoginType.WeGameToken)
                        Model.LoginPage.SelectLoginType(LoginType.WeGameToken);

                    if (!hasUnavailableSecrets && !string.IsNullOrWhiteSpace(account.AutoLoginSessionKey))
                        LoginPassword.Password = MainWindowViewModel.PRESUDO_PASSWORD;
                    break;

                case XIVAccountType.WeGameSID:
                    if (currentLoginType != LoginType.WeGameSID)
                        Model.LoginPage.SelectLoginType(LoginType.WeGameSID);

                    Model.LoginPage.IsReadWegameInfo = false;
                    break;
            }

            Model.LoginPage.Username = account.UserName;
        }
        finally
        {
            _suppressAccountSelectionTracking = false;
        }
    }

    private void LoginPassword_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext != null)
            ((MainWindowViewModel)DataContext).LoginPage.Password = ((PasswordBox)sender).Password;
    }

    private void LoginUsername_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressAccountSelectionTracking)
            return;

        _accountManager.ClearCurrentAccount();
    }

    private void LoginTypeSelection_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAccountSelectionTracking || e.RemovedItems.Count == 0 || e.AddedItems.Count == 0)
            return;

        _accountManager.ClearCurrentAccount();
    }

    private void ShowCredTypeRecoveryMessage()
    {
        var result = App.StartupContext.CredTypeApplyResult;
        if (result is not { WasFallbackApplied: true } || string.IsNullOrWhiteSpace(result.UserMessage))
            return;

        CustomMessageBox.Builder
                        .NewFrom(result.UserMessage)
                        .WithCaption("自动登录加密方式已恢复")
                        .WithParentWindow(this)
                        .Show();
    }

    private void BannerDot_OnChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { DataContext: BannerDotInfo bannerDotInfo })
            return;

        SwitchBanner(bannerDotInfo.Index);
    }

    private void RadioButton_MouseEnter(object sender, MouseEventArgs e)
    {
        _bannerChangeTimer?.Stop();

        if (sender is RadioButton { DataContext: BannerDotInfo bannerDotInfo })
            SwitchBanner(bannerDotInfo.Index);
    }

    private void RadioButton_MouseLeave(object sender, MouseEventArgs e) =>
        _bannerChangeTimer?.Start();

    private void ShowNextBanner()
    {
        if (_banners is not { Length: > 0 })
            return;

        var nextBannerIndex = _currentBannerIndex + 1 > _banners.Length - 1
                                  ? 0
                                  : _currentBannerIndex + 1;

        SwitchBanner(nextBannerIndex);
    }

    private void SwitchBanner(int bannerIndex)
    {
        if (_bannerBitmaps == null || _bannerDotList == null)
            return;

        if (bannerIndex < 0 || bannerIndex >= _bannerBitmaps.Length || bannerIndex >= _bannerDotList.Count)
            return;

        if (_currentBannerIndex == bannerIndex && BannerImage.Source == _bannerBitmaps[bannerIndex])
            return;

        _currentBannerIndex = bannerIndex;
        SetBannerDotActiveState(bannerIndex);
        
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        Timeline.SetDesiredFrameRate(fadeOut, 60);
        var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200));
        Timeline.SetDesiredFrameRate(fadeIn, 60);
        
        fadeOut.Completed += (s, e) =>
        {
            BannerImage.Source = _bannerBitmaps[bannerIndex];
            BannerImage.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        };
        
        BannerImage.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void SetBannerDotActiveState(int activeIndex)
    {
        if (_bannerDotList == null)
            return;

        for (var i = 0; i < _bannerDotList.Count; i++)
            _bannerDotList[i].Active = i == activeIndex;
    }

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

    private void DCTravelPageButton_OnClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Links.DC_TRAVEL_PAGE_URL) { UseShellExecute = true });

    private void OpenExternalSiteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void PayPageButton_OnClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Links.SDO_PAYMENT_URL) { UseShellExecute = true });
    
    private void ShoppingPageButton_OnClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Links.SDO_SHOPPING_URL) { UseShellExecute = true });

    private void RisingStonePageButton_OnClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Links.RISING_STONE_URL) { UseShellExecute = true });
}
