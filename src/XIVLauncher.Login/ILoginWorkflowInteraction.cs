using XIVLauncher.Account;

namespace XIVLauncher.Login;

public interface ILoginWorkflowInteraction
{
    void ShowQrCode(byte[] qrBytes);

    void ShowVerificationCode(string code);

    void ShowLoginMessage(string message);

    string? PromptTextInput(string text, string caption, string initialText);

    string? PromptCaptchaInput(LoginCaptchaChallenge challenge);

    NewAccountDeviceProfileChoice PromptNewAccountDeviceProfileChoice();

    bool ConfigureTemporaryAccountDeviceProfile(XIVAccount account, AccountManager accountManager);

    void ShowError(string message);

    string? GetSavedWeGameLauncherPath();

    void SaveWeGameLauncherPath(string path);

    string? PromptWeGameInstallDirectory(string? currentPath);

    Task<bool> TryElevatedCopyVersionDllAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
}
