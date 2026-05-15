using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using XIVLauncher.Common.Util;
using XIVLauncher.Windows.ViewModel;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows;

public partial class LoadingDialog
{
    public bool IsClosed { get; private set; }

    private LoadingDialogViewModel ViewModel => (LoadingDialogViewModel)DataContext;

    private readonly bool shouldHideFromWindowSwitcher;

    public LoadingDialog()
    {
        InitializeComponent();

        DataContext = new LoadingDialogViewModel();
    }

    public LoadingDialog(string message, bool hideFromTaskSwitcher)
        : this()
    {
        ViewModel.HeaderText         = message;
        shouldHideFromWindowSwitcher = hideFromTaskSwitcher;

        if (!hideFromTaskSwitcher)
            return;

        ShowInTaskbar = false;
        Topmost       = true;

        var interop = new WindowInteropHelper(this);
        interop.EnsureHandle();
    }

    public void SetMessage(string message)
    {
        Dispatcher.Invoke
        (() =>
            {
                if (IsClosed)
                    return;

                ViewModel.IsDetailTextVisible = true;
                ViewModel.DetailText          = message;
            }
        );
    }

    public new void ShowDialog()
    {
        Dispatcher.Invoke
        (() =>
            {
                if (IsClosed)
                    return;

                Show();
            }
        );
    }

    public void HideDialog()
    {
        Dispatcher.Invoke
        (() =>
            {
                if (IsClosed)
                    return;

                Hide();
            }
        );
    }

    public void ReportProgress(long? size, long downloaded, double? progress)
    {
        Dispatcher.Invoke
        (() =>
            {
                if (IsClosed)
                    return;

                if (progress == null || size == null)
                {
                    ViewModel.IsProgressIndeterminate = true;
                    ViewModel.IsPercentageTextVisible = false;
                    ViewModel.PercentageText          = string.Empty;
                }
                else
                {
                    ViewModel.IsProgressIndeterminate = false;
                    ViewModel.ProgressValue           = progress.Value;
                    ViewModel.IsPercentageTextVisible = true;
                    ViewModel.PercentageText          = $"{progress.Value:0}% ({APIHelper.BytesToString(downloaded)}/{APIHelper.BytesToString(size.Value)})";
                }
            }
        );
    }

    public void ReportProgress(int progress)
    {
        Dispatcher.Invoke
        (() =>
            {
                if (IsClosed)
                    return;

                var clampedProgress = Math.Clamp(progress, 0, 100);

                ViewModel.IsProgressIndeterminate = false;
                ViewModel.ProgressValue           = clampedProgress;
                ViewModel.IsPercentageTextVisible = true;
                ViewModel.PercentageText          = $"{clampedProgress}%";
            }
        );
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        IsClosed = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        DragMove();
    }

    private void LoadingDialog_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (shouldHideFromWindowSwitcher)
            HideFromWindowSwitcher.Hide(this);
    }
}
