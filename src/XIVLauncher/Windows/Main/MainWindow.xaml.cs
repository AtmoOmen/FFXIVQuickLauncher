using System.ComponentModel;
using System.Windows;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Common.Http.Site;
using XIVLauncher.Login.WeGame;
using XIVLauncher.Windows.ViewModel.Main;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.Main;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    internal MainWindowViewModel Model => (DataContext as MainWindowViewModel)!;

    private bool everShown;

    public MainWindow()
    {
        InitializeComponent();

        DataContext                                        =  new MainWindowViewModel(this);
        LoginCard.AccountListView.ContextMenu!.DataContext =  Model.AccountSwitcher;

        Model.NewsFlow.NewsItemsUpdated += items => Dispatcher.Invoke(() => NewsList.SetNewsItems(items));
        Model.NewsFlow.BannersUpdated   += bitmaps => Dispatcher.Invoke
        (() =>
            {
                NewsCarousel.UpdateBanners(bitmaps);
                NewsCarousel.StartRotation();
            }
        );
        Model.AccountFlow.LoginPasswordDisplay += password => LoginCard.LoginPassword.Password = password;

        Closed  += MainWindow_OnClosed;
        Closed  += Model.OnWindowClosed;
        Closing += Model.OnWindowClosing;

        Model.Activate += () => Dispatcher.Invoke
        (() =>
            {
                Model.GameUpdateMonitor.QueueCheck();
                Model.NewsFlow.RefreshOnActivate();
                Show();
                Activate();
                Focus();
            }
        );

        Model.Hide += () => Dispatcher.Invoke(HideMainWindow);

        Model.ShowSnackbar += message => Dispatcher.Invoke(() => CopySnackbar.MessageQueue?.Enqueue(message));

        Model.AccountFlow.RequestSwitchToCurrentAccount = () => Dispatcher.Invoke
        (() =>
            {
                if (Model.AccountManager.CurrentAccount is { } account)
                    SwitchAccount(account, false);
            }
        );

        // 订阅控件事件
        NewsCarousel.BannerClicked += Model.NewsFlow.OpenBanner;
        NewsList.NewsClicked       += Model.NewsFlow.OpenNews;
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
        Model.StartupFlow.ApplyStartupDefaults();
        Model.NewsFlow.Start();

        if (App.Settings.GamePath?.Exists != true
            && (!WeGamePathValidator.IsValidGameRoot(App.Settings.WeGamePath?.FullName)
                || !WeGamePathValidator.IsValidSdologinDir(WeGamePathValidator.DeriveSdologinDir(App.Settings.WeGamePath!.FullName))))
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

        Model.GameUpdateMonitor.Start();

        var startupFlow = Model.StartupFlow;

        Task.Run(async () => await startupFlow.RunStartupTasksAsync().ConfigureAwait(false));

        Log.Information("MainWindow initialized.");

        Show();
        Activate();

        Model.StartupFlow.ShowCredTypeRecoveryMessage();

        everShown = true;
        Activated += (_, _) => Model.GameUpdateMonitor.QueueCheck();
    }

    private void SwitchAccount(XIVAccount account, bool saveAsCurrent) =>
        SuppressAccountSelectionTracking(() => Model.AccountFlow.SwitchAccount(account, saveAsCurrent));

    private void OnAccountSwitchRequested(object? sender, EventArgs e) =>
        SuppressAccountSelectionTracking(Model.AccountFlow.SwitchAccountFromSwitcher);

    /// <summary>
    ///     抑制账号选择跟踪, 防止程序化切换账号时误触发清除逻辑
    /// </summary>
    private void SuppressAccountSelectionTracking(Action switchAction)
    {
        LoginCard.SuppressAccountSelectionTracking = true;

        try
        {
            switchAction();
        }
        finally
        {
            LoginCard.SuppressAccountSelectionTracking = false;
        }
    }

    private void OnAccountFieldCopyRequested(object? sender, string text) =>
        Model.AccountFlow.CopyAccountField(text);

    private void OnClearCurrentAccountRequested(object? sender, EventArgs e) =>
        Model.AccountFlow.ClearCurrentAccount();

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
        base.OnSourceInitialized(e);

        try
        {
            PreserveWindowPosition.RestorePosition(this);

            Width  = 780;
            Height = 580;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Couldn't restore window position");
        }
    }

    private void HideMainWindow() =>
        Hide();

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

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        Model.NewsFlow.Stop();
        NewsCarousel.StopRotation();
    }
}
