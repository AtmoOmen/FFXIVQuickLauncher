using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XIVLauncher.Accounts;
using XIVLauncher.Windows.Services;
using XIVLauncher.Windows.ViewModel;
using Point = System.Windows.Point;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for AccountSwitcher.xaml
/// </summary>
public partial class AccountSwitcher : Window
{
    public EventHandler<XIVAccount>? OnAccountSwitchedEventHandler;

    private AccountSwitcherViewModel ViewModel => (AccountSwitcherViewModel)DataContext;

    private Point         _startPoint;
    private ListViewItem? _draggedItem;
    private bool          _closing;

    public AccountSwitcher(AccountManager accountManager)
    {
        InitializeComponent();

        DataContext = new AccountSwitcherViewModel
        (
            accountManager,
            new DialogService(this),
            new ShortcutService()
        );
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        do
        {
            if (current is T ancestor)
                return ancestor;

            current = VisualTreeHelper.GetParent(current);
        }
        while (current != null);

        return null;
    }

    private void AccountListView_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        var selectedAccount = ViewModel.SelectCurrentAccount();
        if (selectedAccount == null)
            return;

        OnAccountSwitchedEventHandler?.Invoke(this, selectedAccount);
        _closing = true;
        Close();
    }

    private void AccountSwitcher_OnDeactivated(object sender, EventArgs e)
    {
        if (!_closing)
            Close();
    }

    private void AccountListView_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint  = e.GetPosition(null);
        _draggedItem = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);

        if (_draggedItem != null)
            _draggedItem.IsSelected = true;
    }

    private void AccountListView_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var mousePosition = e.GetPosition(null);
        var difference    = _startPoint - mousePosition;

        if (sender is not ListView listView
            || FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource) is not ListViewItem listViewItem
            || listView.ItemContainerGenerator.ItemFromContainer(listViewItem) is not AccountSwitcherEntry accountEntry
            || e.LeftButton != MouseButtonState.Pressed
            || _draggedItem == null)
            return;

        if (Math.Abs(difference.X) <= SystemParameters.MinimumHorizontalDragDistance && Math.Abs(difference.Y) <= SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new DataObject("AccountSwitcherEntry", accountEntry);
        DragDrop.DoDragDrop(listViewItem, data, DragDropEffects.Move);
    }

    private void AccountListView_OnDrop(object sender, DragEventArgs e)
    {
        if (_draggedItem == null)
            return;

        var targetItem = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);
        if (targetItem == null)
            return;

        var targetIndex  = AccountListView.ItemContainerGenerator.IndexFromContainer(targetItem);
        var draggedIndex = AccountListView.ItemContainerGenerator.IndexFromContainer(_draggedItem);
        ViewModel.MoveEntry(draggedIndex, targetIndex);
    }
}
