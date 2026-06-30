using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XIVLauncher.Windows.ViewModel.Main.Models;

namespace XIVLauncher.Windows.Main;

/// <summary>
///     新闻轮播横幅控件, 包含横幅图片与圆点指示器, 自行管理轮播定时器与淡入淡出动画
/// </summary>
public partial class NewsCarouselControl
{
    private DispatcherTimer?                     bannerChangeTimer;
    private ObservableCollection<BannerDotInfo>? bannerDotList;
    private BitmapImage[]?                       bannerBitmaps;
    private int                                  currentBannerIndex;
    private bool                                 isBannerRotationActive;

    /// <summary>
    ///     横幅被点击时触发, 参数为当前横幅索引
    /// </summary>
    public event Action<int>? BannerClicked;

    public NewsCarouselControl() =>
        InitializeComponent();

    /// <summary>
    ///     更新横幅图片并初始化圆点指示器, 重置到第一张
    /// </summary>
    public void UpdateBanners(BitmapImage[] bitmaps)
    {
        bannerBitmaps = bitmaps;
        bannerDotList = [];

        for (var i = 0; i < bitmaps.Length; i++)
            bannerDotList.Add(new() { Index = i });

        currentBannerIndex    = 0;
        BannerImage.Source    = bitmaps.Length > 0 ? bitmaps[0] : null;
        BannerDot.ItemsSource = bannerDotList;
        SetBannerDotActiveState(0);
    }

    /// <summary>
    ///     清除横幅, 恢复占位图
    /// </summary>
    public void ClearBanners()
    {
        StopRotation();
        bannerBitmaps         = null;
        bannerDotList         = null;
        BannerImage.Source    = null;
        BannerDot.ItemsSource = null;
    }

    public void StartRotation()
    {
        if (bannerChangeTimer != null || bannerBitmaps is not { Length: > 0 })
            return;

        bannerChangeTimer      =  new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        bannerChangeTimer.Tick += (_, _) => ShowNextBanner();
        bannerChangeTimer.Start();
        isBannerRotationActive = true;
    }

    public void StopRotation()
    {
        isBannerRotationActive = false;

        if (bannerChangeTimer == null)
            return;

        bannerChangeTimer.Stop();
        bannerChangeTimer = null;
    }

    private void BannerCard_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (bannerBitmaps is { Length: > 0 })
            BannerClicked?.Invoke(currentBannerIndex);
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
        StopRotation();

        if (sender is RadioButton { DataContext: BannerDotInfo bannerDotInfo })
            SwitchBanner(bannerDotInfo.Index);
    }

    private void RadioButton_MouseLeave(object sender, MouseEventArgs e) =>
        StartRotation();

    private void ShowNextBanner()
    {
        if (bannerBitmaps is not { Length: > 0 })
            return;

        var nextIndex = currentBannerIndex + 1 > bannerBitmaps.Length - 1
                            ? 0
                            : currentBannerIndex + 1;

        SwitchBanner(nextIndex);
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

        fadeOut.Completed += (_, _) =>
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
}
