using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.WindowsAPICodePack.Dialogs;
using XIVLauncher.Account;
using XIVLauncher.Login.Models;
using XIVLauncher.Login.WeGame;
using XIVLauncher.Login.Workflow;
using XIVLauncher.Windows.Services;

namespace XIVLauncher.Windows.ViewModel.Main.Services;

public sealed class MainWindowLoginUIService
(
    Window             window,
    LoginPageViewModel loginPage
) : ILoginWorkflowUI
{
    public void ShowQRCode
    (
        byte[] qrBytes
    ) =>
        window.Dispatcher.Invoke
        (() =>
            {
                loginPage.QRCodeBitmapImage = qrBytes.ToBitmapImage();
                loginPage.IsQrCodeExpired   = false;
            }
        );

    public void ShowVerificationCode
    (
        string code
    ) =>
        window.Dispatcher.Invoke(() => loginPage.LoginMessage = $"确认码: {code}");

    public void ShowLoginMessage
    (
        string message
    ) =>
        window.Dispatcher.Invoke(() => loginPage.LoginMessage = message);

    public string? PromptTextInput
    (
        string text,
        string caption,
        string initialText
    ) =>
        window.Dispatcher.Invoke(() => new DialogService(window).ShowTextInput(text, caption, initialText, window));

    public string? PromptCaptchaInput
    (
        LoginCaptchaChallenge challenge
    ) =>
        window.Dispatcher.Invoke
        (() =>
            {
                var dialog = new CaptchaInputWindow(challenge);

                if (window.IsVisible)
                {
                    dialog.Owner         = window;
                    dialog.ShowInTaskbar = false;
                }

                return dialog.ShowDialog() == true ?
                           dialog.ResultText :
                           null;
            }
        );

    public NewAccountDeviceProfileChoice PromptNewAccountDeviceProfileChoice() =>
        ToDeviceProfileChoice(PromptDeviceProfileChoice("检测到新账号首次登录，需先确认本次使用的设备信息"));

    public NewAccountDeviceProfileChoice PromptQRCodeDeviceProfileChoice() =>
        ToDeviceProfileChoice(PromptDeviceProfileChoice("扫码登录前需先确认本次使用的设备信息"));

    public bool ConfigureTemporaryAccountDeviceProfile
    (
        XIVAccount     account,
        AccountManager accountManager
    ) =>
        window.Dispatcher.Invoke(() => ShowTemporaryAccountDeviceProfileSettings(account, accountManager));

    public void ShowError
    (
        string message
    ) =>
        CustomMessageBox.Show
        (
            message,
            "XIVLauncherCN (Soil)",
            MessageBoxButton.OK,
            MessageBoxImage.Error,
            parentWindow: window
        );

    public string? GetSavedWeGamePath() =>
        App.Settings.WeGamePath?.FullName;

    public void SaveWeGamePath
    (
        string path
    ) =>
        App.Settings.WeGamePath = new DirectoryInfo(path);

    public string? PromptWeGameInstallDirectory
    (
        string? currentPath
    ) =>
        window.Dispatcher.Invoke(PromptWeGameInstallDirectory);

    public async Task<bool> TryElevatedCopyVersionDllAsync
    (
        string            sourcePath,
        string            destinationPath,
        CancellationToken cancellationToken
    )
    {
        var result = window.Dispatcher.Invoke(PromptElevatedVersionDllCopy);
        if (result != MessageBoxResult.OK)
            return false;

        var startInfo = new ProcessStartInfo
        {
            FileName        = "cmd.exe",
            Arguments       = $"/C copy /Y \"{sourcePath}\" \"{destinationPath}\"",
            Verb            = "runas",
            UseShellExecute = true,
            WindowStyle     = ProcessWindowStyle.Hidden,
            CreateNoWindow  = true
        };

        try
        {
            using var process = Process.Start(startInfo);

            if (process == null)
            {
                ShowError("启动复制进程失败");
                return false;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0 || !WeGameLoginCapturer.HashEquals(sourcePath, destinationPath))
            {
                ShowError("复制 version.dll 失败, 请稍后再试");
                return false;
            }

            return true;
        }
        catch (Win32Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static NewAccountDeviceProfileChoice ToDeviceProfileChoice
    (
        MessageBoxResult result
    ) =>
        result switch
        {
            MessageBoxResult.Yes => NewAccountDeviceProfileChoice.UseShared,
            MessageBoxResult.No  => NewAccountDeviceProfileChoice.ConfigurePerAccount,
            _                    => NewAccountDeviceProfileChoice.Cancel
        };

    private MessageBoxResult PromptDeviceProfileChoice
    (
        string message
    ) =>
        CustomMessageBox.Builder
                        .NewFrom(message)
                        .WithCaption("设备信息")
                        .WithButtons(MessageBoxButton.YesNo)
                        .WithYesButtonText("使用共享设备信息")
                        .WithNoButtonText("配置账号设备信息")
                        .WithDefaultResult(MessageBoxResult.Yes)
                        .WithImage(MessageBoxImage.Question)
                        .WithParentWindow(window)
                        .Show();

    private bool ShowTemporaryAccountDeviceProfileSettings
    (
        XIVAccount     account,
        AccountManager accountManager
    )
    {
        var dialog = new AccountDeviceProfileWindow(account, accountManager, true);

        if (window.IsVisible)
        {
            dialog.Owner         = window;
            dialog.ShowInTaskbar = false;
        }

        return dialog.ShowDialog() == true;
    }

    private static string? PromptWeGameInstallDirectory()
    {
        using var dialog = new CommonOpenFileDialog();
        dialog.Multiselect      = false;
        dialog.IsFolderPicker   = true;
        dialog.EnsurePathExists = true;
        dialog.Title            = "请选择 WeGame 版最终幻想 14 安装目录";

        return dialog.ShowDialog() == CommonFileDialogResult.Ok ?
                   dialog.FileName :
                   null;
    }

    private MessageBoxResult PromptElevatedVersionDllCopy() =>
        CustomMessageBox.Builder
                        .NewFrom("写入 WeGame 安装目录失败, 需要管理员权限\n点击\"确定\"后系统会弹出权限确认窗口, 请同意继续")
                        .WithImage(MessageBoxImage.Warning)
                        .WithButtons(MessageBoxButton.OKCancel)
                        .WithCaption("WeGame 登录")
                        .WithParentWindow(window)
                        .Show();
}
