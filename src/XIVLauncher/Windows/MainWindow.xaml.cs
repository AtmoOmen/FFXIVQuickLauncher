using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Threading;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game;
using XIVLauncher.Login;
using XIVLauncher.Common.Http.Site;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel;
using XIVLauncher.Windows.ViewModel.MainWindow;
using XIVLauncher.Windows.ViewModel.MainWindow.Models;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows;

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

    private DispatcherTimer?                     bannerChangeTimer;
    private DispatcherTimer?                     headlinesRefreshTimer;
    private ObservableCollection<BannerDotInfo>? bannerDotList;
    private Headlines?                           headlines;
    private Banner[]?                            banners;
    private BitmapImage[]?                       bannerBitmaps;
    private int                                  currentBannerIndex;
    private bool                                 isBannerRotationActive;
    private int                                  isRefreshingHeadlines;
    private int                                  pendingHeadlinesRefresh;

    private bool          everShown;
    private bool          suppressAccountSelectionTracking;
    private Point         accountSwitcherDragStartPoint;
    private ListViewItem? draggedAccountSwitcherItem;

    public MainWindow()
    {
        InitializeComponent();

        DataContext                              =  new MainWindowViewModel(this);
        accountManager                           =  Model.AccountManager;
        launcher                                 =  Model.Launcher;
        AccountListView.ContextMenu!.DataContext =  Model.AccountSwitcher;
        Model.Settings.SettingsSaved             += (_, _) => _ = RequestHeadlinesRefreshAsync();

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
            StopBannerRotation();

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

            _ = Dispatcher.BeginInvoke
            (
                new Action
                (() =>
                    {
                        currentBannerIndex    = 0;
                        BannerImage.Source    = bannerBitmaps.Length > 0 ? bannerBitmaps[currentBannerIndex] : null;
                        BannerDot.ItemsSource = bannerDotList;
                        SetBannerDotActiveState(currentBannerIndex);
                        StartBannerRotation();
                    }
                )
            );

            _ = Dispatcher.BeginInvoke(new Action(() => { NewsListView.ItemsSource = headlines.News?.OrderByDescending(n => n.Date).ToList(); }));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not get news");
            StopBannerRotation();
            _ = Dispatcher.BeginInvoke
                (new Action(() => { NewsListView.ItemsSource = new List<News> { new() { Title = "无法获取公告信息", Tag = "DlError" } }; }));
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
        else if (!string.IsNullOrEmpty(item.ID))
            Process.Start(new ProcessStartInfo(Links.SDO_NEWS_ARTICLE_BASE_URL + item.ID) { UseShellExecute = true });

    }

    private void Card_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return)
            return;

        if (Model.IsLoggingIn)
            return;

        Model.LoginPage.StartLoginCommand.Execute(null);
    }



    private void HideMainWindow()
    {
        Hide();
    }

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

    private void AccountListView_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        // 点击行内复制按钮时不触发账号切换
        if (FindAncestor<Button>((DependencyObject)e.OriginalSource) != null)
            return;

        Model.AccountSwitcher.ContextEntry = null;
        var selectedAccount = Model.AccountSwitcher.SelectCurrentAccount();
        if (selectedAccount == null)
            return;

        SwitchAccount(selectedAccount, true);
        Model.SwitchCard(MainWindowViewModel.LoginCardType.MainPage, false);
    }

    private void CopyAccountField_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not Button { Tag: string text } || string.IsNullOrEmpty(text))
            return;

        if (!TrySetClipboardText(text))
            return;

        CopySnackbar.MessageQueue?.Enqueue($"已复制: {text}");
    }

    // 剪贴板可能被其他进程(输入法、剪贴板工具等)短暂占用, 重试几次以规避 CLIPBRD_E_CANT_OPEN
    private static bool TrySetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException) when (attempt < 9)
            {
                Thread.Sleep(10);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "复制账号信息到剪贴板失败");
                return false;
            }
        }

        return false;
    }

    private void AccountListView_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Model.AccountSwitcher.ContextEntry = null;
        accountSwitcherDragStartPoint      = e.GetPosition(null);
        draggedAccountSwitcherItem         = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);

        draggedAccountSwitcherItem?.IsSelected = true;
    }

    private void AccountListView_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource) is not { } listViewItem)
            return;

        Model.AccountSwitcher.ContextEntry = listViewItem.DataContext as AccountSwitcherEntry;
        e.Handled                          = true;

        if (AccountListView.ContextMenu == null)
            return;

        AccountListView.ContextMenu.PlacementTarget = listViewItem;
        AccountListView.ContextMenu.IsOpen          = true;
    }

    private void AccountListView_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var mousePosition = e.GetPosition(null);
        var difference    = accountSwitcherDragStartPoint - mousePosition;

        if (sender is not ListView listView
            || FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource) is not ListViewItem listViewItem
            || listView.ItemContainerGenerator.ItemFromContainer(listViewItem) is not AccountSwitcherEntry accountEntry
            || e.LeftButton               != MouseButtonState.Pressed
            || draggedAccountSwitcherItem == null)
            return;

        if (Math.Abs(difference.X) <= SystemParameters.MinimumHorizontalDragDistance && Math.Abs(difference.Y) <= SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new DataObject("AccountSwitcherEntry", accountEntry);
        DragDrop.DoDragDrop(listViewItem, data, DragDropEffects.Move);
    }

    private void AccountListView_OnDrop(object sender, DragEventArgs e)
    {
        if (draggedAccountSwitcherItem == null)
            return;

        var targetItem = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);
        if (targetItem == null)
            return;

        var targetIndex  = AccountListView.ItemContainerGenerator.IndexFromContainer(targetItem);
        var draggedIndex = AccountListView.ItemContainerGenerator.IndexFromContainer(draggedAccountSwitcherItem);
        Model.AccountSwitcher.MoveEntry(draggedIndex, targetIndex);
    }

    private void AccountListViewContextMenu_OnClosed(object sender, RoutedEventArgs e) =>
        Model.AccountSwitcher.ContextEntry = null;

    private void SwitchAccount(XIVAccount account, bool saveAsCurrent)
    {
        suppressAccountSelectionTracking = true;

        try
        {
            if (saveAsCurrent)
                accountManager.CurrentAccount = account;

            var hasUnavailableSecrets = accountManager.HasUnavailableSecrets(account);
            var selectedArea          = Model.LoginPage.LoginAreas.FirstOrDefault(x => x.AreaName == account.AreaName);

            Model.LoginPage.IsFastLogin = account.QuickLoginEnabled;
            Model.LoginPage.Area        = selectedArea ?? Model.LoginPage.Area;
            LoginPassword.Password      = string.Empty;
            Model.LoginPage.Password    = string.Empty;

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
                        LoginPassword.Password   = MainWindowViewModel.PRESUDO_PASSWORD;
                        Model.LoginPage.Password = MainWindowViewModel.PRESUDO_PASSWORD;
                    }

                    break;

                case XIVAccountType.WeGame:
                    Model.LoginPage.SelectLoginType(LoginType.WeGame);
                    Model.LoginPage.Username = account.WeGameLoginAccount;

                    if (!hasUnavailableSecrets && !string.IsNullOrWhiteSpace(account.WeGameQuickLoginSecret))
                    {
                        LoginPassword.Password   = MainWindowViewModel.PRESUDO_PASSWORD;
                        Model.LoginPage.Password = MainWindowViewModel.PRESUDO_PASSWORD;
                    }
                    else
                        Model.LoginPage.IsReadWegameInfo = false;

                    break;
            }
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

        if (string.IsNullOrWhiteSpace(LoginUsername.Text))
        {
            LoginPassword.Password = string.Empty;
            Model.LoginPage.Password = string.Empty;
        }

        accountManager.ClearCurrentAccount();
        Model.AccountSwitcher.RefreshEntries(null, false);
    }

    private void LoginTypeSelection_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressAccountSelectionTracking || e.RemovedItems.Count == 0 || e.AddedItems.Count == 0)
            return;

        accountManager.ClearCurrentAccount();
        Model.AccountSwitcher.RefreshEntries(null, false);
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

        if (!isBannerRotationActive)
            return;

        SwitchBanner(bannerDotInfo.Index);
    }

    private void RadioButton_MouseEnter(object sender, MouseEventArgs e)
    {
        StopBannerRotation();

        if (sender is RadioButton { DataContext: BannerDotInfo bannerDotInfo })
            SwitchBanner(bannerDotInfo.Index);
    }

    private void RadioButton_MouseLeave(object sender, MouseEventArgs e) =>
        StartBannerRotation();

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

    private void StartBannerRotation()
    {
        if (bannerChangeTimer != null || bannerBitmaps is not { Length: > 0 })
            return;

        bannerChangeTimer      =  new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        bannerChangeTimer.Tick += (_, _) => ShowNextBanner();
        bannerChangeTimer.Start();
        isBannerRotationActive = true;
    }

    private void StopBannerRotation()
    {
        isBannerRotationActive = false;

        if (bannerChangeTimer == null)
            return;

        bannerChangeTimer.Stop();
        bannerChangeTimer = null;
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
            if (headlinesRefreshTimer != null)
            {
                headlinesRefreshTimer.Stop();
                headlinesRefreshTimer.Tick -= HeadlinesRefreshTimer_OnTick;
                headlinesRefreshTimer = null;
            }

            StopBannerRotation();
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
