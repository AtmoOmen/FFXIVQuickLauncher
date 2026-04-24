using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using XIVLauncher.Accounts;
using XIVLauncher.Windows.Services;
using XIVLauncher.Windows.ViewModel;
using Point = System.Windows.Point;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for AccountSwitcher.xaml
/// </summary>
public partial class AccountSwitcher
{
    public event EventHandler<XIVAccount>? AccountSwitched;

    private AccountSwitcherViewModel ViewModel => (AccountSwitcherViewModel)DataContext;

    private Point         dragStartPoint;
    private ListViewItem? draggedItem;
    private bool          closing;

    public AccountSwitcher(AccountManager accountManager, Window? parentWindow = null)
    {
        InitializeComponent();

        DataContext = new AccountSwitcherViewModel
        (
            accountManager,
            new DialogService(parentWindow),
            new ShortcutService(),
            () => CloseWindow(false)
        );

        AccountListView.ContextMenu?.DataContext = DataContext;
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

    private void AccountListView_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        ViewModel.ContextEntry = null;
        var selectedAccount = ViewModel.SelectCurrentAccount();
        if (selectedAccount == null)
            return;

        AccountSwitched?.Invoke(this, selectedAccount);
        CloseWindow(true);
    }

    private void AccountSwitcher_OnDeactivated(object sender, EventArgs e)
    {
        if (closing || AccountListView.ContextMenu?.IsOpen == true)
            return;

        CloseWindow(true);
    }

    public void RefreshSelectedAccount(string? selectedAccountId) =>
        ViewModel.RefreshEntries(selectedAccountId, false);

    public void HideWindow()
    {
        BeginAnimation(OpacityProperty, null);
        BeginAnimation(MarginProperty,  null);
        closing = false;
        Hide();
    }

    public void CloseWindow(bool animate)
    {
        if (closing)
            return;

        closing = true;

        if (!animate)
        {
            HideWindow();
            return;
        }

        var storyboard = new Storyboard();
        var opacityAnimation = new DoubleAnimation
        {
            To             = 0,
            Duration       = TimeSpan.FromSeconds(0.15),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacityAnimation, this);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath("Opacity"));

        var marginAnimation = new ThicknessAnimation
        {
            To             = new Thickness(0, 10, 0, -10),
            Duration       = TimeSpan.FromSeconds(0.15),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(marginAnimation, this);
        Storyboard.SetTargetProperty(marginAnimation, new PropertyPath("Margin"));

        storyboard.Children.Add(opacityAnimation);
        storyboard.Children.Add(marginAnimation);
        storyboard.Completed += (_, _) => { HideWindow(); };
        storyboard.Begin();
    }

    private void AccountListView_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ViewModel.ContextEntry = null;
        dragStartPoint         = e.GetPosition(null);
        draggedItem            = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);

        draggedItem?.IsSelected = true;
    }

    private void AccountListView_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource) is not { } listViewItem)
            return;

        ViewModel.ContextEntry = listViewItem.DataContext as AccountSwitcherEntry;
        e.Handled              = true;

        if (AccountListView.ContextMenu == null)
            return;

        AccountListView.ContextMenu.PlacementTarget = listViewItem;
        AccountListView.ContextMenu.IsOpen          = true;
    }

    private void AccountListView_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var mousePosition = e.GetPosition(null);
        var difference    = dragStartPoint - mousePosition;

        if (sender is not ListView listView
            || FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource) is not ListViewItem listViewItem
            || listView.ItemContainerGenerator.ItemFromContainer(listViewItem) is not AccountSwitcherEntry accountEntry
            || e.LeftButton != MouseButtonState.Pressed
            || draggedItem  == null)
            return;

        if (Math.Abs(difference.X) <= SystemParameters.MinimumHorizontalDragDistance && Math.Abs(difference.Y) <= SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new DataObject("AccountSwitcherEntry", accountEntry);
        DragDrop.DoDragDrop(listViewItem, data, DragDropEffects.Move);
    }

    private void AccountListView_OnDrop(object sender, DragEventArgs e)
    {
        if (draggedItem == null)
            return;

        var targetItem = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);
        if (targetItem == null)
            return;

        var targetIndex  = AccountListView.ItemContainerGenerator.IndexFromContainer(targetItem);
        var draggedIndex = AccountListView.ItemContainerGenerator.IndexFromContainer(draggedItem);
        ViewModel.MoveEntry(draggedIndex, targetIndex);
    }

    private void AccountListViewContextMenu_OnClosed(object sender, RoutedEventArgs e) =>
        ViewModel.ContextEntry = null;
}
