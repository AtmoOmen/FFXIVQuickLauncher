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
    private bool          isHiding;

    public AccountSwitcher(AccountManager accountManager, Window? parentWindow = null)
    {
        InitializeComponent();

        DataContext = new AccountSwitcherViewModel
        (
            accountManager,
            new DialogService(parentWindow),
            new ShortcutService(),
            () => HideWindow(false)
        );

        AccountListView.ContextMenu?.DataContext = DataContext;

        IsVisibleChanged += OnIsVisibleChanged;
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
        HideWindow();
    }

    private void AccountSwitcher_OnDeactivated(object sender, EventArgs e)
    {
        if (AccountListView.ContextMenu?.IsOpen == true || !IsVisible)
            return;

        HideWindow();
    }

    public void RefreshSelectedAccount(string? selectedAccountId) =>
        ViewModel.RefreshEntries(selectedAccountId, false);

    public void HideWindow(bool animate = true)
    {
        if (!animate)
        {
            isHiding = false;
            Hide();
            return;
        }

        if (isHiding)
            return;

        isHiding = true;

        var closeStoryboard = new Storyboard();

        var opacityAnim = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.25))
        {
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(opacityAnim, WindowAnimationLayer);
        Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(UIElement.OpacityProperty));

        var yAnim = new DoubleAnimation(0, 12, TimeSpan.FromSeconds(0.25))
        {
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(yAnim, WindowAnimationLayerTransform);
        Storyboard.SetTargetProperty(yAnim, new PropertyPath(TranslateTransform.YProperty));

        closeStoryboard.Children.Add(opacityAnim);
        closeStoryboard.Children.Add(yAnim);
        closeStoryboard.Completed += (_, _) =>
        {
            WindowAnimationLayer.BeginAnimation(UIElement.OpacityProperty, null);
            WindowAnimationLayerTransform.BeginAnimation(TranslateTransform.YProperty, null);
            WindowAnimationLayer.Opacity = 0;
            WindowAnimationLayerTransform.Y = 20;
            Hide();
        };
        closeStoryboard.Begin();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
            return;

        isHiding = false;

        // Clear stale animation state from previous hide
        WindowAnimationLayer.BeginAnimation(UIElement.OpacityProperty, null);
        WindowAnimationLayerTransform.BeginAnimation(TranslateTransform.YProperty, null);
        WindowAnimationLayer.Opacity = 0;
        WindowAnimationLayerTransform.Y = 20;

        var showStoryboard = new Storyboard();

        var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.35))
        {
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacityAnim, WindowAnimationLayer);
        Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(UIElement.OpacityProperty));

        var yAnim = new DoubleAnimation(20, 0, TimeSpan.FromSeconds(0.45))
        {
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(yAnim, WindowAnimationLayerTransform);
        Storyboard.SetTargetProperty(yAnim, new PropertyPath(TranslateTransform.YProperty));

        showStoryboard.Children.Add(opacityAnim);
        showStoryboard.Children.Add(yAnim);
        showStoryboard.Begin();
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
