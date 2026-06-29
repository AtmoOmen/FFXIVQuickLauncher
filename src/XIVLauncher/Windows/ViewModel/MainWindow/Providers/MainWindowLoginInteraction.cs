using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using XIVLauncher.Account;
using XIVLauncher.Login;
using XIVLauncher.Windows.Services;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Providers;

public sealed class MainWindowLoginInteraction
(
    Window                   window,
    LoginPageViewModel       loginPage,
    MainWindowDialogProvider dialogProvider
) : ILoginWorkflowInteraction
{
    public void ShowQrCode(byte[] qrBytes) =>
        window.Dispatcher.Invoke
        (() =>
            {
                loginPage.QRCodeBitmapImage = qrBytes.ToBitmapImage();
                loginPage.IsQrCodeExpired   = false;
            }
        );

    public void ShowVerificationCode(string code) =>
        window.Dispatcher.Invoke(() => loginPage.LoginMessage = $"确认码: {code}");

    public void ShowLoginMessage(string message) =>
        window.Dispatcher.Invoke(() => loginPage.LoginMessage = message);

    public string? PromptTextInput(string text, string caption, string initialText) =>
        window.Dispatcher.Invoke(() => new DialogService(window).ShowTextInput(text, caption, initialText, window));

    public string? PromptCaptchaInput(LoginCaptchaChallenge challenge) =>
        window.Dispatcher.Invoke
        (() =>
            {
                var dialog = new CaptchaInputWindow(challenge);

                if (window.IsVisible)
                {
                    dialog.Owner         = window;
                    dialog.ShowInTaskbar = false;
                }

                return dialog.ShowDialog() == true ? dialog.ResultText : null;
            }
        );

    public NewAccountDeviceProfileChoice PromptNewAccountDeviceProfileChoice() =>
        dialogProvider.PromptNewAccountDeviceProfileChoice() switch
        {
            MessageBoxResult.Yes => NewAccountDeviceProfileChoice.UseShared,
            MessageBoxResult.No  => NewAccountDeviceProfileChoice.ConfigurePerAccount,
            _                    => NewAccountDeviceProfileChoice.Cancel
        };

    public bool ConfigureTemporaryAccountDeviceProfile(XIVAccount account, AccountManager accountManager) =>
        dialogProvider.ShowTemporaryAccountDeviceProfileSettings(account, accountManager);

    public void ShowError(string message) =>
        CustomMessageBox.Show
        (
            message,
            "XIVLauncherCN (Soil)",
            MessageBoxButton.OK,
            MessageBoxImage.Error,
            parentWindow: window
        );

    public string? GetSavedWeGameLauncherPath() =>
        App.Settings.WeGameLauncherPath;

    public void SaveWeGameLauncherPath(string path) =>
        App.Settings.WeGameLauncherPath = path;

    public string? PromptWeGameInstallDirectory(string? currentPath) =>
        window.Dispatcher.Invoke(MainWindowDialogProvider.PromptWeGameInstallDirectory);

    public async Task<bool> TryElevatedCopyVersionDllAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var result = window.Dispatcher.Invoke(dialogProvider.PromptElevatedVersionDllCopy);
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
}
