using System.Windows;
using System.Windows.Controls;

namespace XIVLauncher.Windows.Main.Slides;

/// <summary>
///     Dashboard 页面, 展示当前账号、大区选择和启动游戏按钮
/// </summary>
public partial class DashboardSlide
{
    /// <summary>
    ///     请求复制账号字段到剪贴板
    /// </summary>
    public event EventHandler<string>? AccountFieldCopyRequested;

    public DashboardSlide() =>
        InitializeComponent();

    private void CopyAccountField_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not Button { Tag: string text } || string.IsNullOrEmpty(text))
            return;

        AccountFieldCopyRequested?.Invoke(this, text);
    }
}
