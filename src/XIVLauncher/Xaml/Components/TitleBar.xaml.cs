using System;
using System.Windows;
using System.Windows.Controls;
using MaterialDesignThemes.Wpf;

namespace XIVLauncher.Xaml.Components;

public partial class TitleBar : UserControl
{
    private Window? hostWindow;

    public TitleBar()
    {
        InitializeComponent();

        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        hostWindow = Window.GetWindow(this);
        if (hostWindow == null)
            return;

        hostWindow.StateChanged += HostWindow_StateChanged;

        UpdateCaptionButtons();
        UpdateMaximizeIcon();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (hostWindow != null)
            hostWindow.StateChanged -= HostWindow_StateChanged;

        hostWindow = null;
    }

    private void HostWindow_StateChanged(object? sender, EventArgs e) =>
        UpdateMaximizeIcon();

    private void UpdateCaptionButtons()
    {
        if (hostWindow == null)
            return;

        var canMinimize = hostWindow.ResizeMode is ResizeMode.CanMinimize or ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
        var canMaximize = hostWindow.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;

        MinimizeButton.Visibility = canMinimize ? Visibility.Visible : Visibility.Collapsed;
        MaximizeButton.Visibility = canMaximize ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMaximizeIcon()
    {
        if (hostWindow == null)
            return;

        var isMaximized = hostWindow.WindowState == WindowState.Maximized;
        MaximizeIcon.Kind   = isMaximized ? PackIconKind.WindowRestore : PackIconKind.WindowMaximize;
        MaximizeButton.ToolTip = isMaximized ? "还原" : "最大化";
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = hostWindow ?? Window.GetWindow(this);
        if (window != null)
            SystemCommands.MinimizeWindow(window);
    }

    private void MaximizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = hostWindow ?? Window.GetWindow(this);
        if (window == null)
            return;

        if (window.WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(window);
        else
            SystemCommands.MaximizeWindow(window);
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = hostWindow ?? Window.GetWindow(this);
        window?.Close();
    }
}
