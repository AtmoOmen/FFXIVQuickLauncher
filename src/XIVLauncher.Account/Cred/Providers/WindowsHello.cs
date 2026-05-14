using System.Runtime.InteropServices;
using System.Text;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Serilog;

namespace XIVLauncher.Account.Cred.Providers;

public class WindowsHello
(
    CredData cred
) : ICredProvider
{
    private string CredName => $"{cred.PackageName}-{cred.Account}";

    public string GetName() => "WindowsHello";

    public string GetDescription() => "使用生物识别身份验证加密, 可以使用面部、虹膜、指纹或 PIN 码";

    private EncryptionHelper? encryptionHelper;

    public async Task<string?> Decrypt(string? text) =>
        text is null ? null : (await GetEncryptionHelper()).DecryptString(text);

    public async Task<string?> Encrypt(string? text) =>
        text is null ? null : (await GetEncryptionHelper()).EncryptString(text);

    public async Task<bool> IsSupported()
    {
        try
        {
            return await KeyCredentialManager.IsSupportedAsync();
        }
        catch
        {
            return false;
        }
    }

    public async Task Unregister() =>
        await KeyCredentialManager.DeleteAsync(CredName);

    public Task ClearCache()
    {
        encryptionHelper = null;
        return Task.CompletedTask;
    }

    public async Task<EncryptionHelper> GetEncryptionHelper()
    {
        if (encryptionHelper is not null)
            return encryptionHelper;

        _ = Task.Run
        (async () =>
            {
                try
                {
                    for (var currentTry = 0; currentTry < MAX_FOCUS_TRIES; currentTry++)
                    {
                        var windowHandle = FindWindow(SECURITY_PROMPT_CLASS_NAME, IntPtr.Zero);

                        if (windowHandle != IntPtr.Zero)
                        {
                            SetForegroundWindow(windowHandle);
                            return;
                        }

                        await Task.Delay(FOCUS_RETRY_DELAY_MS);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("Windows Hello 提示窗口聚焦失败: {Message}", ex.Message);
                }
            }
        );

        var credResult = await KeyCredentialManager.OpenAsync(CredName);
        if (credResult.Status == KeyCredentialStatus.NotFound)
            credResult = await KeyCredentialManager.RequestCreateAsync(CredName, KeyCredentialCreationOption.FailIfExists);

        if (credResult.Status != KeyCredentialStatus.Success)
            throw new InvalidOperationException($"Windows Hello 身份验证失败: {credResult.Status}");

        ArgumentNullException.ThrowIfNull(credResult.Credential);
        var buffer = CryptographicBuffer.ConvertStringToBinary(cred.PasswordProtectedKey, BinaryStringEncoding.Utf8);
        var result = await credResult.Credential.RequestSignAsync(buffer);
        if (result.Status != KeyCredentialStatus.Success)
            throw new InvalidOperationException($"Windows Hello 签名失败: {result.Status}");

        var signedResult = CryptographicBuffer.EncodeToBase64String(result.Result);
        if (string.IsNullOrEmpty(signedResult))
            throw new InvalidOperationException("Windows Hello 签名结果为空");

        encryptionHelper = new EncryptionHelper(Encoding.UTF8.GetBytes(signedResult), Convert.FromBase64String(cred.LoginSalt));
        return encryptionHelper;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, IntPtr windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    #region Constants

    private const int    FOCUS_RETRY_DELAY_MS       = 500;
    private const int    MAX_FOCUS_TRIES            = 3;
    private const string SECURITY_PROMPT_CLASS_NAME = "Credential Dialog Xaml Host";

    #endregion
}
