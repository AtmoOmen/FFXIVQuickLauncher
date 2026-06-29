using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XIVLauncher.Windows.ViewModel;
using XIVLauncher.Windows.ViewModel.MainWindow;

namespace XIVLauncher.Windows.Main.Slides;

/// <summary>
///     账号管理器页面, 展示账号列表并支持拖拽排序和右键菜单
/// </summary>
public partial class AccountSwitcherSlide
{
    // 拖拽相关字段
    private Point         accountSwitcherDragStartPoint;
    private ListViewItem? draggedAccountSwitcherItem;

    /// <summary>
    ///     暴露 ListView 供父窗口设置 ContextMenu DataContext
    /// </summary>
    public ListView ListView => AccountListView;

    /// <summary>
    ///     请求切换账号 (由账号列表点击触发)
    /// </summary>
    public event EventHandler? AccountSwitchRequested;

    /// <summary>
    ///     请求复制账号字段到剪贴板
    /// </summary>
    public event EventHandler<string>? AccountFieldCopyRequested;

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public AccountSwitcherSlide() =>
        InitializeComponent();

    private void SearchButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
            return;

        ViewModel.AccountSwitcher.IsSearchMode = true;
        Dispatcher.BeginInvoke
        (
            DispatcherPriority.Loaded,
            () =>
            {
                SearchTextBox.Focus();
                SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
            }
        );
    }

    private void CloseSearchButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
            return;

        Keyboard.ClearFocus();
        ViewModel.AccountSwitcher.IsSearchMode = false;
    }

    private void SearchTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        Keyboard.ClearFocus();
        if (ViewModel != null)
            ViewModel.AccountSwitcher.IsSearchMode = false;
        e.Handled = true;
    }

    private void CopyAccountField_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not Button { Tag: string text } || string.IsNullOrEmpty(text))
            return;

        AccountFieldCopyRequested?.Invoke(this, text);
    }

    private void AccountListView_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        // 点击行内复制按钮时不触发账号切换
        if (FindAncestor<Button>((DependencyObject)e.OriginalSource) != null)
            return;

        AccountSwitchRequested?.Invoke(this, e);
    }

    private void AccountListView_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel != null)
            ViewModel.AccountSwitcher.ContextEntry = null;

        accountSwitcherDragStartPoint = e.GetPosition(null);
        draggedAccountSwitcherItem    = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);

        if (draggedAccountSwitcherItem != null)
            draggedAccountSwitcherItem.IsSelected = true;
    }

    private void AccountListView_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource) is not { } listViewItem)
            return;

        if (ViewModel != null)
            ViewModel.AccountSwitcher.ContextEntry = listViewItem.DataContext as AccountSwitcherEntry;

        e.Handled = true;

        if (AccountListView.ContextMenu == null)
            return;

        AccountListView.ContextMenu.PlacementTarget = listViewItem;
        AccountListView.ContextMenu.IsOpen          = true;
    }

    private void AccountListView_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var mousePosition = e.GetPosition(null);
        var difference    = accountSwitcherDragStartPoint - mousePosition;

        if (sender is not ListView listView                                                                          ||
            FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource) is not ListViewItem listViewItem          ||
            listView.ItemContainerGenerator.ItemFromContainer(listViewItem) is not AccountSwitcherEntry accountEntry ||
            e.LeftButton               != MouseButtonState.Pressed                                                   ||
            draggedAccountSwitcherItem == null)
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
        ViewModel?.AccountSwitcher.MoveEntry(draggedIndex, targetIndex);
    }

    private void AccountListViewContextMenu_OnClosed(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            ViewModel.AccountSwitcher.ContextEntry = null;
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
}
