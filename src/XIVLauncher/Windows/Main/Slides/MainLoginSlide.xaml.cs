using System.Windows;
using System.Windows.Controls;
using XIVLauncher.Windows.ViewModel;
using XIVLauncher.Windows.ViewModel.Main;

namespace XIVLauncher.Windows.Main.Slides;

/// <summary>
///     主登录页面, 包含登录方式选择、用户名密码输入和登录按钮
/// </summary>
public partial class MainLoginSlide
{
    /// <summary>
    ///     是否抑制账号选择跟踪 (切换账号时由父窗口设置)
    /// </summary>
    public bool SuppressAccountSelectionTracking { get; set; }

    /// <summary>
    ///     暴露密码框供父窗口设置密码
    /// </summary>
    public PasswordBox PasswordBox => LoginPassword;

    /// <summary>
    ///     暴露用户名输入框供父窗口检查文本
    /// </summary>
    public TextBox UsernameBox => LoginUsername;

    /// <summary>
    ///     请求清除当前账号 (由用户名输入或登录方式变更触发)
    /// </summary>
    public event EventHandler? ClearCurrentAccountRequested;

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public MainLoginSlide() =>
        InitializeComponent();

    private void LoginTypeSelection_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuppressAccountSelectionTracking || e.RemovedItems.Count == 0 || e.AddedItems.Count == 0)
            return;

        ClearCurrentAccountRequested?.Invoke(this, e);
    }

    private void LoginUsername_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (SuppressAccountSelectionTracking)
            return;

        if (string.IsNullOrWhiteSpace(LoginUsername.Text))
        {
            LoginPassword.Password        = string.Empty;
            ViewModel?.LoginPage.Password = string.Empty;
        }

        ClearCurrentAccountRequested?.Invoke(this, e);
    }

    private void LoginPassword_OnPasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel?.LoginPage.Password = ((PasswordBox)sender).Password;
}
