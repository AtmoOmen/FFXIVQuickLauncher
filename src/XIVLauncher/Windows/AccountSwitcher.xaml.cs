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
    private Window?       trackedParentWindow;

    public AccountSwitcher(AccountManager accountManager, Window? parentWindow = null)
    {
        InitializeComponent();
        trackedParentWindow = parentWindow;

        DataContext = new AccountSwitcherViewModel
        (
            accountManager,
            new DialogService(parentWindow),
            new ShortcutService(),
            () => CloseWindow(animate: false)
        );

        AccountListView.ContextMenu?.DataContext = DataContext;

        Loaded += (_, _) => ToggleParentWindowTracking(track: true);
        Closed += (_, _) => ToggleParentWindowTracking(track: false);
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

        var selectedAccount = ViewModel.SelectCurrentAccount();
        if (selectedAccount == null)
            return;

        AccountSwitched?.Invoke(this, selectedAccount);
        CloseWindow(animate: true);
    }

    private void AccountSwitcher_OnDeactivated(object sender, EventArgs e)
    {
        if (closing || AccountListView.ContextMenu?.IsOpen == true)
            return;

        CloseWindow(animate: true);
    }

    private void ToggleParentWindowTracking(bool track)
    {
        if (trackedParentWindow == null)
            return;

        if (track)
        {
            trackedParentWindow.StateChanged     += ParentWindow_OnStateChanged;
            trackedParentWindow.IsVisibleChanged += ParentWindow_OnIsVisibleChanged;
            return;
        }

        trackedParentWindow.StateChanged     -= ParentWindow_OnStateChanged;
        trackedParentWindow.IsVisibleChanged -= ParentWindow_OnIsVisibleChanged;
        trackedParentWindow                  =  null;
    }

    private void ParentWindow_OnStateChanged(object? sender, EventArgs e)
    {
        if (trackedParentWindow?.WindowState != WindowState.Minimized)
            return;

        CloseWindow(animate: true);
    }

    private void ParentWindow_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not false)
            return;

        CloseWindow(animate: true);
    }

    private void CloseWindow(bool animate)
    {
        if (closing)
            return;

        closing = true;

        if (!animate)
        {
            Close();
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
        storyboard.Completed += (_, _) => Close();
        storyboard.Begin();
    }

    private void AccountListView_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStartPoint = e.GetPosition(null);
        draggedItem    = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);

        draggedItem?.IsSelected = true;
    }

    private void AccountListView_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource) is { } listViewItem)
        {
            AccountListView.SelectedItem = listViewItem.DataContext;
            listViewItem.IsSelected      = true;
        }
    }

    private void AccountListView_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var mousePosition = e.GetPosition(null);
        var difference    = dragStartPoint - mousePosition;

        if (sender is not ListView listView
            || FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource) is not ListViewItem listViewItem
            || listView.ItemContainerGenerator.ItemFromContainer(listViewItem) is not AccountSwitcherEntry accountEntry
            || e.LeftButton != MouseButtonState.Pressed
            || draggedItem == null)
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
}
