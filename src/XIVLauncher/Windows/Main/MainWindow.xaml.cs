using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Http.Site;
using XIVLauncher.Login;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.Main;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan HEADLINES_REFRESH_INTERVAL = TimeSpan.FromMinutes(10);

    internal MainWindowViewModel Model => (DataContext as MainWindowViewModel)!;

    private const int CURRENT_VERSION_LEVEL = 2;

    private readonly AccountManager accountManager;
    private readonly Launcher       launcher;

    private DispatcherTimer? headlinesRefreshTimer;
    private Headlines?       headlines;
    private Banner[]?        banners;

    private int isRefreshingHeadlines;
    private int pendingHeadlinesRefresh;

    private bool everShown;

    public MainWindow()
    {
        InitializeComponent();

        DataContext                                        =  new MainWindowViewModel(this);
        accountManager                                     =  Model.AccountManager;
        launcher                                           =  Model.Launcher;
        LoginCard.AccountListView.ContextMenu!.DataContext =  Model.AccountSwitcher;
        Model.Settings.SettingsSaved                       += (_, _) => _ = RequestHeadlinesRefreshAsync();

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

        Model.Hide += () => Dispatcher.Invoke(HideMainWindow);

        Model.ReloadHeadlines += () => _ = RequestHeadlinesRefreshAsync();

        // 订阅控件事件
        NewsCarousel.BannerClicked             += OnBannerClicked;
        NewsList.NewsClicked                   += OnNewsClicked;
        LoginCard.SettingsRequested            += OnSettingsRequested;
        LoginCard.AccountSwitchRequested       += OnAccountSwitchRequested;
        LoginCard.AccountFieldCopyRequested    += OnAccountFieldCopyRequested;
        LoginCard.ClearCurrentAccountRequested += OnClearCurrentAccountRequested;

        NewsList.SetNewsItems
        (
            new List<News>
            {
                new()
                {
                    Title = "加载中…",
                    Tag   = "DlError"
                }
            }
        );

        Title += " v" + AppUtil.GetAssemblyVersion();
    }

    public void Initialize()
    {
        SetDefaults();
        EnsureHeadlinesRefreshTimer();

        Model.LoginPage.IsFastLogin = App.Settings.FastLogin;

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
                await SetupServers().ConfigureAwait(false);
                Dispatcher.Invoke
                (() =>
                    {
                        if (accountManager.CurrentAccount is { } savedAccount)
                            SwitchAccount(savedAccount, false);
                    }
                );

                await RequestHeadlinesRefreshAsync().ConfigureAwait(false);
                Troubleshooting.LogTroubleshooting();
            }
        );

        Log.Information("MainWindow initialized.");

        Show();
        Activate();

        ShowCredTypeRecoveryMessage();

        everShown = true;
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
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
                if (areas.Length == 0)
                    areas = [new LoginArea { AreaName = "获取大区失败", AreaID = "-1" }];

                Model.LoginPage.LoginAreas = [.. areas];
                Model.LoginPage.Area       = Model.LoginPage.LoginAreas[0];
            }
        );
    }

    private void EnsureHeadlinesRefreshTimer()
    {
        if (headlinesRefreshTimer != null)
            return;

        headlinesRefreshTimer      =  new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = HEADLINES_REFRESH_INTERVAL };
        headlinesRefreshTimer.Tick += HeadlinesRefreshTimer_OnTick;
        headlinesRefreshTimer.Start();
    }

    private async void HeadlinesRefreshTimer_OnTick(object? sender, EventArgs e) =>
        await RequestHeadlinesRefreshAsync().ConfigureAwait(false);

    private async Task RequestHeadlinesRefreshAsync()
    {
        Volatile.Write(ref pendingHeadlinesRefresh, 1);

        if (Interlocked.CompareExchange(ref isRefreshingHeadlines, 1, 0) != 0)
            return;

        try
        {
            do
            {
                Interlocked.Exchange(ref pendingHeadlinesRefresh, 0);
                await SetupHeadlines().ConfigureAwait(false);
            }
            while (Volatile.Read(ref pendingHeadlinesRefresh) != 0);
        }
        finally
        {
            Interlocked.Exchange(ref isRefreshingHeadlines, 0);

            if (Volatile.Read(ref pendingHeadlinesRefresh) != 0)
                await RequestHeadlinesRefreshAsync().ConfigureAwait(false);
        }
    }

    private async Task SetupHeadlines()
    {
        try
        {
            NewsCarousel.StopRotation();

            headlines = await Headlines.GetHeadlinesAsync(launcher)
                                       .ConfigureAwait(false);
            banners = headlines.Banner;

            var bannerBitmaps = new BitmapImage[banners.Length];

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
            }

            _ = Dispatcher.BeginInvoke
            (
                new Action
                (() =>
                    {
                        NewsCarousel.UpdateBanners(bannerBitmaps);
                        NewsCarousel.StartRotation();
                    }
                )
            );

            _ = Dispatcher.BeginInvoke(new Action(() => { NewsList.SetNewsItems(headlines.News?.OrderByDescending(n => n.Date).ToList() ?? new List<News>()); }));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not get news");
            NewsCarousel.StopRotation();
            _ = Dispatcher.BeginInvoke
                (new Action(() => { NewsList.SetNewsItems(new List<News> { new() { Title = "无法获取公告信息", Tag = "DlError" } }); }));
        }
    }

    private static void SetDefaults()
    {
        var versionLevel = App.Settings.VersionUpgradeLevel;

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
                            App.Settings.DalamudInjectionDelayMS = 4000;
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

    private void OnBannerClicked(int bannerIndex)
    {
        if (headlines != null)
            Process.Start(new ProcessStartInfo(banners![bannerIndex].Link.ToString()) { UseShellExecute = true });
    }

    private static void OnNewsClicked(News item)
    {
        if (!string.IsNullOrEmpty(item.Url))
            Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true });
        else if (!string.IsNullOrEmpty(item.ID))
            Process.Start(new ProcessStartInfo(Links.SDO_NEWS_ARTICLE_BASE_URL + item.ID) { UseShellExecute = true });
    }

    private void HideMainWindow() =>
        Hide();

    private void OnAccountSwitchRequested(object? sender, EventArgs e)
    {
        var selectedAccount = Model.AccountSwitcher.SelectCurrentAccount();
        if (selectedAccount == null)
            return;

        SwitchAccount(selectedAccount, true);
        Model.SwitchCard(MainWindowViewModel.LoginCardType.MainPage, false);
    }

    private void OnAccountFieldCopyRequested(object? sender, string text)
    {
        var copyThread = new Thread
        (() =>
            {
                var copied = TrySetClipboardText(text);
                Dispatcher.BeginInvoke
                (() =>
                     CopySnackbar.MessageQueue?.Enqueue(copied ? $"已复制: {text}" : "复制失败, 剪贴板被占用")
                );
            }
        )
        {
            IsBackground = true,
            Name         = "ClipboardCopy"
        };
        copyThread.Start();
    }

    private void OnClearCurrentAccountRequested(object? sender, EventArgs e)
    {
        accountManager.ClearCurrentAccount();
        Model.AccountSwitcher.RefreshEntries(null, false);
    }

    private static bool TrySetClipboardText(string text)
    {
        const int MAX_ATTEMPTS   = 40;
        const int RETRY_DELAY_MS = 50;

        for (var attempt = 0; attempt < MAX_ATTEMPTS; attempt++)
        {
            if (TrySetClipboardTextOnce(text))
                return true;

            Thread.Sleep(RETRY_DELAY_MS);
        }

        Log.Warning("复制账号信息到剪贴板失败: 剪贴板持续被占用");
        return false;
    }

    private static bool TrySetClipboardTextOnce(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
            return false;

        try
        {
            if (!EmptyClipboard())
                return false;

            // CF_UNICODETEXT 要求 GMEM_MOVABLE 全局内存, 含结尾 null 字符
            var byteCount = (text.Length + 1) * 2;
            var hGlobal   = GlobalAlloc(GMEM_MOVABLE, (UIntPtr)byteCount);
            if (hGlobal == IntPtr.Zero)
                return false;

            var target = GlobalLock(hGlobal);

            if (target == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
            {
                // 设置失败时系统未接管内存, 需自行释放
                GlobalFree(hGlobal);
                return false;
            }

            // 设置成功后系统接管 hGlobal, 不可再释放
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVABLE   = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private void SwitchAccount(XIVAccount account, bool saveAsCurrent)
    {
        LoginCard.SuppressAccountSelectionTracking = true;

        try
        {
            if (saveAsCurrent)
                accountManager.CurrentAccount = account;

            var hasUnavailableSecrets = accountManager.HasUnavailableSecrets(account);
            var selectedArea          = Model.LoginPage.LoginAreas.FirstOrDefault(x => x.AreaName == account.AreaName);

            Model.LoginPage.IsFastLogin      = account.QuickLoginEnabled;
            Model.LoginPage.Area             = selectedArea ?? Model.LoginPage.Area;
            LoginCard.LoginPassword.Password = string.Empty;
            Model.LoginPage.Password         = string.Empty;

            switch (account.AccountType)
            {
                case XIVAccountType.Sdo:
                    var nextLoginType = !hasUnavailableSecrets && !string.IsNullOrWhiteSpace(account.SdoPassword)
                                            ? LoginType.Static
                                            : LoginType.Slide;

                    Model.LoginPage.SelectLoginType(nextLoginType);
                    Model.LoginPage.Username = account.UserName;

                    if (nextLoginType == LoginType.Static)
                    {
                        // Make users happy by not showing their password
                        LoginCard.LoginPassword.Password = MainWindowViewModel.PRESUDO_PASSWORD;
                        Model.LoginPage.Password         = MainWindowViewModel.PRESUDO_PASSWORD;
                    }

                    break;

                case XIVAccountType.WeGame:
                    Model.LoginPage.SelectLoginType(LoginType.WeGame);
                    Model.LoginPage.Username = account.WeGameLoginAccount;

                    if (!hasUnavailableSecrets && !string.IsNullOrWhiteSpace(account.WeGameQuickLoginSecret))
                    {
                        LoginCard.LoginPassword.Password = MainWindowViewModel.PRESUDO_PASSWORD;
                        Model.LoginPage.Password         = MainWindowViewModel.PRESUDO_PASSWORD;
                    }
                    else
                        Model.LoginPage.IsReadWegameInfo = false;

                    break;
            }
        }
        finally
        {
            LoginCard.SuppressAccountSelectionTracking = false;
        }
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

    private void MainWindow_OnClosing(object sender, CancelEventArgs e)
    {
        if (!everShown)
            return;

        try
        {
            if (headlinesRefreshTimer != null)
            {
                headlinesRefreshTimer.Stop();
                headlinesRefreshTimer.Tick -= HeadlinesRefreshTimer_OnTick;
                headlinesRefreshTimer      =  null;
            }

            NewsCarousel.StopRotation();
            PreserveWindowPosition.SaveWindowPosition(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Couldn't save window position");
        }
    }

    private void DCTravelPageButton_OnClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Links.DC_TRAVEL_PAGE_URL) { UseShellExecute = true });

    private void RisingStonePageButton_OnClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Links.RISING_STONE_URL) { UseShellExecute = true });
}
