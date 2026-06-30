using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MaterialDesignThemes.Wpf;
using XIVLauncher.Windows.ViewModel;
using XIVLauncher.Windows.ViewModel.Main;

namespace XIVLauncher.Windows.Main.Slides;

/// <summary>
///     Dashboard 页面, 展示当前账号、大区选择和启动游戏按钮
/// </summary>
public partial class DashboardSlide
{
    private DateTime menuClosedAt = DateTime.MinValue;

    /// <summary>
    ///     请求复制账号字段到剪贴板
    /// </summary>
    public event EventHandler<string>? AccountFieldCopyRequested;

    public DashboardSlide() =>
        InitializeComponent();

    private void AccountCard_OnClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Card { ContextMenu: { } menu })
            return;

        if ((DateTime.Now - menuClosedAt).TotalMilliseconds < 200)
            return;

        if (menu.IsOpen)
        {
            menu.IsOpen = false;
            return;
        }

        menu.Closed           += OnMenuClosed;
        menu.PlacementTarget  =  (UIElement)sender;
        menu.Placement        =  PlacementMode.Bottom;
        menu.HorizontalOffset =  0;
        menu.VerticalOffset   =  4;
        menu.IsOpen           =  true;
        e.Handled             =  true;
    }

    private void OnMenuClosed(object sender, EventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Closed  -= OnMenuClosed;
            menuClosedAt =  DateTime.Now;
        }
    }

    private void CopyAccountField_OnMenuItemClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (DataContext is not MainWindowViewModel { DashboardPage.AccountName: var name } || string.IsNullOrEmpty(name))
            return;

        AccountFieldCopyRequested?.Invoke(this, name);
    }
}
