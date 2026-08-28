using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Http.Site;

namespace XIVLauncher.Windows.ViewModel.Main.Flows;

/// <summary>
///     主界面新闻列表与轮播横幅的定时刷新流
/// </summary>
internal sealed class NewsFlow
{
    private static readonly TimeSpan REFRESH_INTERVAL  = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ACTIVATE_COOLDOWN = TimeSpan.FromMinutes(5);

    private readonly MainWindowViewModel vm;
    private readonly Launcher           launcher;

    private DispatcherTimer? headlinesRefreshTimer;
    private Headlines?       headlines;
    private Banner[]?        banners;
    private DateTimeOffset   lastActivateRefresh;

    private int isRefreshingHeadlines;
    private int pendingHeadlinesRefresh;

    public NewsFlow(MainWindowViewModel vm)
    {
        this.vm       = vm;
        this.launcher = vm.Launcher;
    }

    /// <summary>
    ///     由 View 赋值, 刷新新闻列表时更新界面
    /// </summary>
    public Action<IEnumerable<News>>? NewsItemsUpdated { get; set; }

    /// <summary>
    ///     由 View 赋值, 刷新轮播横幅时更新界面
    /// </summary>
    public Action<BitmapImage[]>? BannersUpdated { get; set; }

    public void OpenBanner(int bannerIndex)
    {
        if (banners is not { } bannerItems)
            return;

        OpenUrl(bannerItems[bannerIndex].Link.ToString());
    }

    public void OpenNews(News item)
    {
        if (!string.IsNullOrEmpty(item.Url))
            OpenUrl(item.Url);
        else if (!string.IsNullOrEmpty(item.ID))
            OpenUrl(Links.SDO_NEWS_ARTICLE_BASE_URL + item.ID);
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public void Start()
    {
        if (headlinesRefreshTimer != null)
            return;

        headlinesRefreshTimer      =  new DispatcherTimer(DispatcherPriority.Background, vm.Window.Dispatcher) { Interval = REFRESH_INTERVAL };
        headlinesRefreshTimer.Tick += HeadlinesRefreshTimer_OnTick;
        headlinesRefreshTimer.Start();
    }

    public void Stop()
    {
        if (headlinesRefreshTimer == null)
            return;

        headlinesRefreshTimer.Stop();
        headlinesRefreshTimer.Tick -= HeadlinesRefreshTimer_OnTick;
        headlinesRefreshTimer      =  null;
    }

    public void RefreshNow() =>
        _ = RefreshHeadlinesAsync();

    public async Task RefreshHeadlinesAsync()
    {
        Volatile.Write(ref pendingHeadlinesRefresh, 1);

        if (Interlocked.CompareExchange(ref isRefreshingHeadlines, 1, 0) != 0)
            return;

        try
        {
            do
            {
                Interlocked.Exchange(ref pendingHeadlinesRefresh, 0);
                await SetupNews().ConfigureAwait(false);
            }
            while (Volatile.Read(ref pendingHeadlinesRefresh) != 0);
        }
        finally
        {
            Interlocked.Exchange(ref isRefreshingHeadlines, 0);

            if (Volatile.Read(ref pendingHeadlinesRefresh) != 0)
                await RefreshHeadlinesAsync().ConfigureAwait(false);
        }
    }

    public void RefreshOnActivate()
    {
        if (DateTimeOffset.UtcNow - lastActivateRefresh < ACTIVATE_COOLDOWN)
            return;

        lastActivateRefresh = DateTimeOffset.UtcNow;
        RefreshNow();
    }

    private async void HeadlinesRefreshTimer_OnTick(object? sender, EventArgs e) =>
        await RefreshHeadlinesAsync().ConfigureAwait(false);

    private async Task SetupNews()
    {
        Headlines? refreshedHeadlines;

        try
        {
            refreshedHeadlines = await Headlines.GetHeadlinesAsync(launcher).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not get news");

            if (headlines == null)
            {
                NewsItemsUpdated?.Invoke(new List<News> { new() { Title = "无法获取公告信息", Tag = "DlError" } });
            }
            else
            {
                vm.ShowSnackbar("新闻刷新失败, 稍后自动重试");
            }

            return;
        }

        headlines = refreshedHeadlines;

        var newsItems = refreshedHeadlines.News?.OrderByDescending(n => n.Date).ToList() ?? new List<News>();

        NewsItemsUpdated?.Invoke(newsItems);
        Log.Information("新闻已刷新, 共 {NewsCount} 条", newsItems.Count);
        await SetupBanners(refreshedHeadlines.Banner).ConfigureAwait(false);
    }

    private async Task SetupBanners(Banner[] bannerItems)
    {
        if (bannerItems.Length == 0)
            return;

        var bannerBitmaps = new BitmapImage[bannerItems.Length];

        try
        {
            await Task.WhenAll
            (
                Enumerable.Range(0, bannerItems.Length)
                          .Select
                          (async bannerIndex =>
                              {
                                  var imageBytes = await launcher.DownloadAsLauncher(bannerItems[bannerIndex].LsbBanner.ToString()).ConfigureAwait(false);

                                  using var stream = new MemoryStream(imageBytes);

                                  var bitmapImage = new BitmapImage();
                                  bitmapImage.BeginInit();
                                  bitmapImage.StreamSource = stream;
                                  bitmapImage.CacheOption  = BitmapCacheOption.OnLoad;
                                  bitmapImage.EndInit();
                                  bitmapImage.Freeze();

                                  bannerBitmaps[bannerIndex] = bitmapImage;
                              }
                          )
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "轮播图下载失败, 保留当前轮播");
            return;
        }

        banners = bannerItems;
        BannersUpdated?.Invoke(bannerBitmaps);
        Log.Information("轮播已刷新, 共 {BannerCount} 张", bannerBitmaps.Length);
    }
}
