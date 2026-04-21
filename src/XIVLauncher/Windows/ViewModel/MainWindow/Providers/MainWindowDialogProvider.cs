using System.Windows;
using XIVLauncher.Accounts;

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

    public void ShowRepairBlockedMessage()
    {
        CustomMessageBox.Builder
                        .NewFrom("官方启动器或游戏正在运行中, 无法执行修复, 请关闭相关进程后重试")
                        .WithImage(MessageBoxImage.Exclamation)
                        .WithParentWindow(window)
                        .Show();
    }

    public bool ConfirmRepairGame() =>
        CustomMessageBox.Builder
                        .NewFrom("XIVLauncher 将会搜寻任何与原版不一致的游戏文件并替换修复\n这可能导致使用 TexTools 安装的模组被还原\n持续时间可能较长, 请确认是否要继续?")
                        .WithButtons(MessageBoxButton.YesNo)
                        .WithImage(MessageBoxImage.Question)
                        .WithParentWindow(window)
                        .Show()
        != MessageBoxResult.No;

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
        var dialog = new AccountDeviceProfileSettingsWindow(account, accountManager, true);

        if (window.IsVisible)
        {
            dialog.Owner         = window;
            dialog.ShowInTaskbar = false;
        }

        return dialog.ShowDialog() == true;
    }
}
