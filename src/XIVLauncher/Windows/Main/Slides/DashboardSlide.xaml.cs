using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using XIVLauncher.Windows.ViewModel.Main;

namespace XIVLauncher.Windows.Main.Slides;

/// <summary>
///     Dashboard 页面, 展示当前账号、大区选择、启动游戏操作与官方快捷服务
/// </summary>
public partial class DashboardSlide
{
    private DateTime accountMenuClosedAt;
    private DateTime launchMenuClosedAt;
    private DateTime socialPopupClosedAt;

    /// <summary>
    ///     请求复制账号字段到剪贴板
    /// </summary>
    public event EventHandler<string>? AccountFieldCopyRequested;

    public DashboardSlide() =>
        InitializeComponent();

    private void AccountInfoButton_OnClick
    (
        object          sender,
        RoutedEventArgs e
    )
    {
        if (AccountContextMenu is not { } menu)
            return;

        if ((DateTime.Now - accountMenuClosedAt).TotalMilliseconds < 180)
            return;

        if (menu.IsOpen)
        {
            menu.IsOpen = false;
            return;
        }

        menu.Closed           += OnAccountMenuClosed;
        menu.PlacementTarget  =  AccountInfoButton;
        menu.Placement        =  PlacementMode.Bottom;
        menu.HorizontalOffset =  0;
        menu.VerticalOffset   =  4;
        menu.IsOpen           =  true;
        e.Handled             =  true;
    }

    private void OnAccountMenuClosed
    (
        object    sender,
        EventArgs e
    )
    {
        if (sender is not ContextMenu menu)
            return;

        menu.Closed         -= OnAccountMenuClosed;
        accountMenuClosedAt =  DateTime.Now;
    }

    private void LaunchOptionsButton_OnClick
    (
        object          sender,
        RoutedEventArgs e
    )
    {
        if (LaunchOptionsContextMenu is not { } menu)
            return;

        if ((DateTime.Now - launchMenuClosedAt).TotalMilliseconds < 180)
            return;

        if (menu.IsOpen)
        {
            menu.IsOpen = false;
            return;
        }

        menu.Closed           += OnLaunchMenuClosed;
        menu.PlacementTarget  =  LaunchSplitCard;
        menu.Placement        =  PlacementMode.Bottom;
        menu.HorizontalOffset =  0;
        menu.VerticalOffset   =  4;
        menu.IsOpen           =  true;
        e.Handled             =  true;
    }

    private void OnLaunchMenuClosed
    (
        object    sender,
        EventArgs e
    )
    {
        if (sender is not ContextMenu menu)
            return;

        menu.Closed        -= OnLaunchMenuClosed;
        launchMenuClosedAt =  DateTime.Now;
    }

    private void OfficialAccountsButton_OnClick
    (
        object          sender,
        RoutedEventArgs e
    )
    {
        if ((DateTime.Now - socialPopupClosedAt).TotalMilliseconds < 180)
            return;

        OfficialAccountsPopup.IsOpen = !OfficialAccountsPopup.IsOpen;
    }

    private void OfficialAccountsPopup_OnClosed
    (
        object?   sender,
        EventArgs e
    ) =>
        socialPopupClosedAt = DateTime.Now;

    private void SocialSiteMenuItem_OnClick
    (
        object          sender,
        RoutedEventArgs e
    ) =>
        OfficialAccountsPopup.IsOpen = false;

    private void CopyAccountField_OnMenuItemClick
    (
        object          sender,
        RoutedEventArgs e
    )
    {
        e.Handled = true;

        if (DataContext is not MainWindowViewModel { DashboardPage.AccountName: var name } || string.IsNullOrEmpty(name))
            return;

        AccountFieldCopyRequested?.Invoke(this, name);
    }
}
