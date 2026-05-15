using System.Windows;
using XIVLauncher.Account;
using XIVLauncher.Login;
using XIVLauncher.Windows.Services;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Providers;

public sealed class MainWindowLoginInteraction
(
    Window window,
    LoginPageViewModel loginPage,
    MainWindowDialogProvider dialogProvider
) : ILoginWorkflowInteraction
{
    public void ShowQrCode(byte[] qrBytes)
    {
        window.Dispatcher.Invoke
        (() =>
            {
                loginPage.QRCodeBitmapImage = qrBytes.ToBitmapImage();
                loginPage.IsQrCodeExpired   = false;
            }
        );
    }

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
}
