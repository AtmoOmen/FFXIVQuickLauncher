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
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using XIVLauncher.Accounts;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
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
public partial class MainWindow
{
    public MainWindowViewModel Model => (DataContext as MainWindowViewModel)!;

    private const int CURRENT_VERSION_LEVEL = 2;

    private readonly AccountManager  accountManager;
    private readonly Launcher        launcher;
    private readonly AccountSwitcher accountSwitcher;

    private Timer?                               bannerChangeTimer;
    private ObservableCollection<BannerDotInfo>? bannerDotList;
    private Headlines?                           headlines;
    private Banner[]?                            banners;
    private BitmapImage[]?                       bannerBitmaps;
    private int                                  currentBannerIndex;

    private bool everShown;
    private bool suppressAccountSelectionTracking;

    public MainWindow()
    {
        InitializeComponent();

        DataContext                  =  new MainWindowViewModel(this);
        accountManager               =  Model.AccountManager;
        launcher                     =  Model.Launcher;
        Model.Settings.SettingsSaved += (_, _) => Task.Run(SetupHeadlines);

        accountSwitcher = new AccountSwitcher(accountManager, this)
        {
            ShowInTaskbar = false,
            ShowActivated = false
        };

        accountSwitcher.Closing += (s, ev) =>
        {
            ev.Cancel = true;
            accountSwitcher.CloseWindow(false);
        };
        accountSwitcher.AccountSwitched += OnAccountSwitchedEventHandler;
        Model.AttachAccountSwitcher(accountSwitcher);

        Closed  += Model.OnWindowClosed;
        Closing += Model.OnWindowClosing;

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

        Title += " v" + AppUtil.GetAssemblyVersion();
    }

    public void Initialize()
    {
        SetDefaults();

        Model.LoginPage.IsFastLogin = App.Settings.FastLogin;

        var savedAccount          = accountManager.CurrentAccount;
        var hasUnavailableSecrets = accountManager.HasUnavailableSecrets(savedAccount);

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

            Model.Settings.ReloadFromSettings();
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
                    }
                );

                await SetupHeadlines();
                Troubleshooting.LogTroubleshooting();
            }
        );

        Log.Information("MainWindow initialized.");

        Show();
        Activate();

        ShowCredTypeRecoveryMessage();

        everShown = true;
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(Model.Settings)
        {
            Owner = this
        };

        window.ShowDialog();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        try
        {
            PreserveWindowPosition.RestorePosition(this);

            Width  = 780;
            Height = 540;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Couldn't restore window position");
        }
    }

    private async Task SetupServers()
    {
        var areas = new LoginArea[] { new() { AreaName = "获取大区失败", AreaID = "-1" } };
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
            bannerChangeTimer?.Stop();

            headlines = await Headlines.GetHeadlinesAsync(launcher)
                                       .ConfigureAwait(false);
            banners = headlines.Banner;

            bannerBitmaps = new BitmapImage[banners.Length];
            bannerDotList = [];

            for (var i = 0; i < banners.Length; i++)
            {
                var imageBytes = await launcher.DownloadAsLauncher(banners[i].LsbBanner.ToString());

                using var stream = new MemoryStream(imageBytes);

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = stream;
                bitmapImage.CacheOption  = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                bannerBitmaps[i] = bitmapImage;
                bannerDotList.Add(new() { Index = i });
            }

            currentBannerIndex = 0;
            SetBannerDotActiveState(currentBannerIndex);

            _ = Dispatcher.BeginInvoke
            (
                new Action
                (() =>
                    {
                        BannerImage.Source    = bannerBitmaps[currentBannerIndex];
                        BannerDot.ItemsSource = bannerDotList;
                    }
                )
            );

            bannerChangeTimer = new Timer { Interval = 5000 };

            bannerChangeTimer.Elapsed += (_, _) => Dispatcher.BeginInvoke(new Action(ShowNextBanner), DispatcherPriority.Background);

            bannerChangeTimer.AutoReset = true;
            bannerChangeTimer.Start();

            _ = Dispatcher.BeginInvoke(new Action(() => { NewsListView.ItemsSource = headlines.News?.OrderByDescending(n => n.Date).ToList(); }));
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

        App.Settings.AskBeforePatchInstall                       ??= true;
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

        if (headlines != null) Process.Start(new ProcessStartInfo(banners![currentBannerIndex].Link.ToString()) { UseShellExecute = true });
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

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);

        if (Model.IsAccountSwitcherVisible)
        {
            if (e.OriginalSource is DependencyObject depObj && !AccountSwitcherButton.IsAncestorOf(depObj) && !Equals(e.OriginalSource, AccountSwitcherButton))
                Model.CloseAccountSwitcher(true);
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);

        if (Model.IsAccountSwitcherVisible)
            Model.CloseAccountSwitcher(false);
    }

    private void OnAccountSwitchedEventHandler(object? sender, XIVAccount e) =>
        SwitchAccount(e, true);

    private void SwitchAccount(XIVAccount account, bool saveAsCurrent)
    {
        suppressAccountSelectionTracking = true;

        try
        {
            if (saveAsCurrent)
                accountManager.CurrentAccount = account;

            var hasUnavailableSecrets = accountManager.HasUnavailableSecrets(account);
            var currentLoginType      = Model.LoginPage.LoginTypeOption.LoginType;

            Model.LoginPage.IsFastLogin = account.AutoLogin;
            Model.LoginPage.Area        = Model.LoginPage.LoginAreas.FirstOrDefault(x => x.AreaName == account.AreaName)!;
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
            suppressAccountSelectionTracking = false;
        }
    }

    private void LoginPassword_OnPasswordChanged(object sender, RoutedEventArgs e) =>
        ((MainWindowViewModel)DataContext)?.LoginPage.Password = ((PasswordBox)sender).Password;

    private void LoginUsername_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressAccountSelectionTracking)
            return;

        accountManager.ClearCurrentAccount();
    }

    private void LoginTypeSelection_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressAccountSelectionTracking || e.RemovedItems.Count == 0 || e.AddedItems.Count == 0)
            return;

        accountManager.ClearCurrentAccount();
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
        bannerChangeTimer?.Stop();

        if (sender is RadioButton { DataContext: BannerDotInfo bannerDotInfo })
            SwitchBanner(bannerDotInfo.Index);
    }

    private void RadioButton_MouseLeave(object sender, MouseEventArgs e) =>
        bannerChangeTimer?.Start();

    private void ShowNextBanner()
    {
        if (banners is not { Length: > 0 })
            return;

        var nextBannerIndex = currentBannerIndex + 1 > banners.Length - 1
                                  ? 0
                                  : currentBannerIndex + 1;

        SwitchBanner(nextBannerIndex);
    }

    private void SwitchBanner(int bannerIndex)
    {
        if (bannerBitmaps == null || bannerDotList == null)
            return;

        if (bannerIndex < 0 || bannerIndex >= bannerBitmaps.Length || bannerIndex >= bannerDotList.Count)
            return;

        if (currentBannerIndex == bannerIndex && BannerImage.Source == bannerBitmaps[bannerIndex])
            return;

        currentBannerIndex = bannerIndex;
        SetBannerDotActiveState(bannerIndex);

        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        var fadeIn  = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200));

        fadeOut.Completed += (s, e) =>
        {
            BannerImage.Source = bannerBitmaps[bannerIndex];
            BannerImage.BeginAnimation(OpacityProperty, fadeIn);
        };

        BannerImage.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void SetBannerDotActiveState(int activeIndex)
    {
        if (bannerDotList == null)
            return;

        for (var i = 0; i < bannerDotList.Count; i++)
            bannerDotList[i].Active = i == activeIndex;
    }

    private void MainWindow_OnClosing(object sender, CancelEventArgs e)
    {
        if (!everShown)
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
