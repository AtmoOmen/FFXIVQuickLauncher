using System.Windows;
using XIVLauncher.Account;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Providers;

public sealed class MainWindowDialogProvider
(
    Window window
)
{
    public void ShowAutoLoginDisclaimer()
    {
        CustomMessageBox.Builder
                        .NewFrom("自动登录已启用, 后续将默认使用当前账号登录, 并不再显示主窗口\n若需要修改设置, 请在登录时按住 SHIFT 键")
                        .WithParentWindow(window)
                        .Show();
    }

    public MessageBoxResult PromptNewAccountDeviceProfileChoice() =>
        CustomMessageBox.Builder
                        .NewFrom("检测到新账号首次登录，需先确认本次使用的设备信息")
                        .WithCaption("设备信息")
                        .WithButtons(MessageBoxButton.YesNo)
                        .WithYesButtonText("使用共享设备信息")
                        .WithNoButtonText("配置账号设备信息")
                        .WithDefaultResult(MessageBoxResult.Yes)
                        .WithImage(MessageBoxImage.Question)
                        .WithParentWindow(window)
                        .Show();

    public bool ShowTemporaryAccountDeviceProfileSettings(XIVAccount account, AccountManager accountManager)
    {
        var dialog = new AccountDeviceProfileWindow(account, accountManager, true);

        if (window.IsVisible)
        {
            dialog.Owner         = window;
            dialog.ShowInTaskbar = false;
        }

        return dialog.ShowDialog() == true;
    }

    public string? PromptWeGameInstallDirectory()
    {
        using var dialog = new CommonOpenFileDialog
        {
            Multiselect      = false,
            IsFolderPicker   = true,
            EnsurePathExists = true,
            Title            = "请选择 WeGame 版最终幻想 14 安装目录"
        };

        return dialog.ShowDialog() == CommonFileDialogResult.Ok
                   ? dialog.FileName
                   : null;
    }

    public MessageBoxResult PromptElevatedVersionDllCopy() =>
        CustomMessageBox.Builder
                        .NewFrom("写入 WeGame 安装目录失败, 需要管理员权限\n点击\"确定\"后系统会弹出权限确认窗口, 请同意继续")
                        .WithImage(MessageBoxImage.Warning)
                        .WithButtons(MessageBoxButton.OKCancel)
                        .WithCaption("WeGame 登录")
                        .WithParentWindow(window)
                        .Show();
}
