using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Util;
using XIVLauncher.Windows.Services;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel;

internal sealed class FirstTimeSetupViewModel : INotifyPropertyChanged
{
    public ICommand NextCommand { get; }

    public string GamePath
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public bool EnableDalamud
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    public int CurrentStepIndex
    {
        get;
        set => SetProperty(ref field, value);
    }

    public           bool             WasCompleted { get; private set; }
    private readonly IDialogService   _dialogService;
    private readonly IShortcutService _shortcutService;

    public FirstTimeSetupViewModel(IDialogService? dialogService = null, IShortcutService? shortcutService = null)
    {
        _dialogService   = dialogService   ?? new DialogService();
        _shortcutService = shortcutService ?? new ShortcutService();

        NextCommand = new SyncCommand(_ => MoveNext());

        GamePath = Paths.GetGamePath() ?? string.Empty;
    }

    public void EnsureDesktopShortcut()
    {
        try
        {
            var desktop      = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var launcherPath = Paths.ResolveExecutablePath();

            _shortcutService.CreateShortcut(desktop, "XIVLauncherCN (Soil)", launcherPath);
        }
        catch
        {
            _dialogService.ShowMessage
            (
                "创建桌面快捷方式失败，如有需要请稍后手动创建。",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation
            );
        }
    }

    public bool MoveNext()
    {
        switch (CurrentStepIndex)
        {
            case 0:
                if (!ValidateGamePath())
                    return false;

                CurrentStepIndex++;
                return true;

            case 1:
                App.Settings.Update
                (settings =>
                    {
                        settings.GamePath       = new DirectoryInfo(GamePath);
                        settings.DalamudEnabled = EnableDalamud;
                        settings.AddonList      = [];
                    }
                );

                WasCompleted = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return true;

            default:
                return false;
        }
    }

    private bool ValidateGamePath()
    {
        if (string.IsNullOrWhiteSpace(GamePath))
        {
            _dialogService.ShowMessage
            (
                "请选择游戏所在目录。",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                false,
                false
            );
            return false;
        }

        if (!GameHelpers.LetChoosePath(GamePath))
        {
            _dialogService.ShowMessage
            (
                "请选择游戏根目录，不要直接选到 Game 子目录。",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            return false;
        }

        if (!GameHelpers.IsValidGamePath(GamePath))
        {
            var result = _dialogService.ShowMessage
            (
                "当前目录中没有检测到游戏安装，是否继续？你也可以稍后登录时再安装游戏。",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.YesNo
            );

            if (result != MessageBoxResult.Yes)
                return false;
        }

        if (GamePath.StartsWith('C'))
        {
            var result = _dialogService.ShowMessage
            (
                "你选择的游戏目录位于 C 盘。XIVLauncherCN 可能无法正常登录，建议将游戏移动到其他磁盘，或以管理员身份运行启动器。是否继续？",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

            if (result != MessageBoxResult.Yes)
                return false;
        }

        return true;
    }

    public event EventHandler? CloseRequested;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}
