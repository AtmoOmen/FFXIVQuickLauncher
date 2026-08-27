using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Util;
using XIVLauncher.Login.WeGame;
using XIVLauncher.Windows.Services;

namespace XIVLauncher.Windows.ViewModel;

internal sealed partial class FirstTimeSetupViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string GamePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WeGamePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PatchPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool EnableDalamud { get; set; } = true;

    [ObservableProperty]
    public partial int CurrentStepIndex { get; set; }

    public bool WasCompleted { get; private set; }

    private readonly IDialogService   _dialogService;
    private readonly IShortcutService _shortcutService;

    public FirstTimeSetupViewModel
    (
        IDialogService?   dialogService     = null,
        IShortcutService? shortcutService   = null,
        string?           initialGamePath   = null,
        string?           initialWeGamePath = null,
        string?           initialPatchPath  = null
    )
    {
        _dialogService   = dialogService   ?? new DialogService();
        _shortcutService = shortcutService ?? new ShortcutService();

        GamePath   = initialGamePath   ?? string.Empty;
        WeGamePath = initialWeGamePath ?? string.Empty;
        PatchPath  = initialPatchPath  ?? Paths.ResolvePatchPath(null, Paths.RoamingPath).FullName;
    }

    [RelayCommand]
    public void MoveNext()
    {
        switch (CurrentStepIndex)
        {
            case 0:
                if (!ValidatePaths())
                    return;

                CurrentStepIndex++;
                return;

            case 1:
                App.Settings.Update
                (settings =>
                    {
                        settings.GamePath = string.IsNullOrWhiteSpace(GamePath) ?
                                                null! :
                                                new DirectoryInfo(GamePath);
                        settings.WeGamePath = string.IsNullOrWhiteSpace(WeGamePath) ?
                                                  null :
                                                  new DirectoryInfo(WeGamePath);
                        settings.PatchPath      = new DirectoryInfo(PatchPath);
                        settings.DalamudEnabled = EnableDalamud;
                    }
                );

                EnsureDesktopShortcut();
                WasCompleted = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;

            default:
                return;
        }
    }

    [RelayCommand]
    public void MoveBack()
    {
        if (CurrentStepIndex > 0)
            CurrentStepIndex--;
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

    private bool ValidatePaths()
    {
        if (string.IsNullOrWhiteSpace(GamePath) && string.IsNullOrWhiteSpace(WeGamePath))
        {
            _dialogService.ShowMessage
            (
                "请至少选择一个游戏目录。",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                false,
                false
            );
            return false;
        }

        if (!string.IsNullOrWhiteSpace(GamePath) && !ValidateGamePath())
            return false;

        if (!string.IsNullOrWhiteSpace(WeGamePath) &&
            (!WeGamePathValidator.IsValidGameRoot(WeGamePath) || !WeGamePathValidator.IsValidSdologinDir(WeGamePathValidator.DeriveSdologinDir(WeGamePath))))
        {
            _dialogService.ShowMessage
            (
                "未找到 WeGame 游戏标记或 sdologin.exe, 请重新选择游戏根目录。",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                false,
                false
            );
            return false;
        }

        return ValidatePatchPath();
    }

    private bool ValidatePatchPath()
    {
        if (string.IsNullOrWhiteSpace(PatchPath))
        {
            _dialogService.ShowMessage
            (
                "请选择补丁存储目录。",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                false,
                false
            );
            return false;
        }

        DirectoryInfo patchDirectory;

        try
        {
            patchDirectory = new DirectoryInfo(PatchPath);
        }
        catch (Exception)
        {
            _dialogService.ShowMessage
            (
                "补丁存储目录格式无效, 请重新选择。",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                false,
                false
            );
            return false;
        }

        var normalizedPatchPath = Path.TrimEndingDirectorySeparator(patchDirectory.FullName);
        var normalizedGamePath = string.IsNullOrWhiteSpace(GamePath) ?
                                     string.Empty :
                                     Path.TrimEndingDirectorySeparator(new DirectoryInfo(GamePath).FullName);
        var normalizedWeGamePath = string.IsNullOrWhiteSpace(WeGamePath) ?
                                       string.Empty :
                                       Path.TrimEndingDirectorySeparator(new DirectoryInfo(WeGamePath).FullName);

        if (string.Equals(normalizedGamePath,   normalizedPatchPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedWeGamePath, normalizedPatchPath, StringComparison.OrdinalIgnoreCase))
        {
            _dialogService.ShowMessage
            (
                "游戏目录和补丁目录不能相同, 请重新选择。",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                false,
                false
            );
            return false;
        }

        return true;
    }

    private bool ValidateGamePath()
    {
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
}
