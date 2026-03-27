using System;
using System.Timers;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;
using XIVLauncher.Windows.ViewModel;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows;
// TODO(goat): Dispatcher!!

/// <summary>
///     Interaction logic for DalamudLoadingOverlay.xaml
/// </summary>
public partial class DalamudLoadingOverlay : Window, IDalamudLoadingOverlay
{
    public  bool                           IsClosed  { get; private set; }
    private DalamudLoadingOverlayViewModel ViewModel => (DalamudLoadingOverlayViewModel)DataContext;

    private IDalamudLoadingOverlay.DalamudUpdateStep _progress;

    public DalamudLoadingOverlay()
    {
        InitializeComponent();

        DataContext = new DalamudLoadingOverlayViewModel();

        var interop = new WindowInteropHelper(this);
        interop.EnsureHandle();
    }

    public void SetStep(IDalamudLoadingOverlay.DalamudUpdateStep progress)
    {
        Dispatcher.Invoke
        (() =>
            {
                _progress = progress;

                switch (progress)
                {
                    case IDalamudLoadingOverlay.DalamudUpdateStep.Dalamud:
                        ViewModel.ProgressText = "正在更新核心...";
                        break;

                    case IDalamudLoadingOverlay.DalamudUpdateStep.Assets:
                        ViewModel.ProgressText = "正在更新资源文件...";
                        break;

                    case IDalamudLoadingOverlay.DalamudUpdateStep.Runtime:
                        ViewModel.ProgressText = "正在更新依赖库...";
                        break;

                    case IDalamudLoadingOverlay.DalamudUpdateStep.Starting:
                        ViewModel.ProgressText = "正在启动...";
                        break;

                    case IDalamudLoadingOverlay.DalamudUpdateStep.Unavailable:
                        ViewModel.ProgressText         = "由于游戏更新，插件目前暂时不可用";
                        ViewModel.IsProgressBarVisible = false;
                        ViewModel.UpdateText           = string.Empty;
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(progress), progress, null);
                }
            }
        );
    }

    public void SetVisible()
    {
        Dispatcher.Invoke
        (() =>
            {
                if (IsClosed)
                    return;

                if (_progress == IDalamudLoadingOverlay.DalamudUpdateStep.Unavailable)
                {
                    var t = new Timer(15000) { AutoReset = false };
                    t.Elapsed += (_, _) => { Dispatcher.Invoke(Close); };
                    t.Start();
                }

                Show();
            }
        );
    }

    public void SetInvisible()
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
                    ViewModel.PercentageText          = string.Empty;
                }
                else
                {
                    ViewModel.IsProgressIndeterminate = false;
                    ViewModel.ProgressValue           = progress.Value;
                    ViewModel.PercentageText          = $"{progress.Value:0}% ({APIHelper.BytesToString(downloaded)}/{APIHelper.BytesToString(size.Value)})";
                }
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

    private void DalamudLoadingOverlay_OnLoaded(object sender, RoutedEventArgs e) =>
        HideFromWindowSwitcher.Hide(this);
}
