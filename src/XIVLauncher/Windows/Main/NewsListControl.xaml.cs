using System.Collections;
using System.Windows.Input;
using XIVLauncher.Common.Http.Site;

namespace XIVLauncher.Windows.Main;

/// <summary>
///     新闻列表控件, 展示新闻条目并在点击时触发事件
/// </summary>
public partial class NewsListControl
{
    /// <summary>
    ///     新闻被点击时触发, 参数为被点击的 News
    /// </summary>
    public event Action<News>? NewsClicked;

    public NewsListControl() =>
        InitializeComponent();

    /// <summary>
    ///     设置新闻列表数据源
    /// </summary>
    public void SetNewsItems(IEnumerable items) =>
        NewsListView.ItemsSource = items;

    private void NewsListView_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (NewsListView.SelectedItem is not News item)
            return;

        NewsClicked?.Invoke(item);
    }
}
