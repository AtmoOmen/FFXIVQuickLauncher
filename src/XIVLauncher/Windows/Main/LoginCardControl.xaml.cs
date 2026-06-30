using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XIVLauncher.Common.Constant;
using XIVLauncher.Windows.ViewModel;
using XIVLauncher.Windows.ViewModel.Main;

namespace XIVLauncher.Windows.Main;

/// <summary>
///     登录卡片壳层 UserControl, 组合各 Slide 子控件并转发事件
/// </summary>
public partial class LoginCardControl
{
    /// <summary>
    ///     是否抑制账号选择跟踪 (切换账号时由父窗口设置), 转发给 MainLoginSlide
    /// </summary>
    public bool SuppressAccountSelectionTracking
    {
        get => MainLogin.SuppressAccountSelectionTracking;
        set => MainLogin.SuppressAccountSelectionTracking = value;
    }

    /// <summary>
    ///     暴露密码框供父窗口设置密码, 转发自 MainLoginSlide
    /// </summary>
    public PasswordBox LoginPassword => MainLogin.PasswordBox;

    /// <summary>
    ///     暴露账号列表供父窗口设置 ContextMenu DataContext, 转发自 AccountSwitcherSlide
    /// </summary>
    public ListView AccountListView => AccountSwitcher.ListView;

    /// <summary>
    ///     请求打开软件设置
    /// </summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    ///     请求切换账号 (由账号列表点击触发)
    /// </summary>
    public event EventHandler? AccountSwitchRequested;

    /// <summary>
    ///     请求复制账号字段到剪贴板
    /// </summary>
    public event EventHandler<string>? AccountFieldCopyRequested;

    /// <summary>
    ///     请求清除当前账号 (由用户名输入或登录方式变更触发)
    /// </summary>
    public event EventHandler? ClearCurrentAccountRequested;

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public LoginCardControl()
    {
        InitializeComponent();

        // 转发子控件事件
        MainLogin.ClearCurrentAccountRequested    += (_, e) => ClearCurrentAccountRequested?.Invoke(this, e);
        Dashboard.AccountFieldCopyRequested       += (_, e) => AccountFieldCopyRequested?.Invoke(this, e);
        AccountSwitcher.AccountSwitchRequested    += (_, e) => AccountSwitchRequested?.Invoke(this, e);
        AccountSwitcher.AccountFieldCopyRequested += (_, e) => AccountFieldCopyRequested?.Invoke(this, e);
    }

    // ── 工具栏 ──

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, e);

    // ── 卡片快捷键 ──

    private void Card_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return)
            return;

        if (ViewModel is not { IsLoggingIn: false })
            return;

        ViewModel.LoginPage.StartLoginCommand.Execute(null);
    }

    // ── 快捷入口 ──

    private void OpenExternalSiteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void PayPageButton_OnClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Links.SDO_PAYMENT_URL) { UseShellExecute = true });

    private void ShoppingPageButton_OnClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Links.SDO_SHOPPING_URL) { UseShellExecute = true });
}
