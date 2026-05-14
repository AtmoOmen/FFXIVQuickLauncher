using System.Net;
using System.Text;

namespace XIVLauncher.Account.Cred.Providers;

public class CredentialManager
(
    CredData cred
) : ICredProvider
{
    private EncryptionHelper? encryptionHelper;
    
    public string GetName() => "凭据管理器";

    public string GetDescription() => "系统自带的凭据管理器";

    public async Task ClearCache() =>
        encryptionHelper = null;

    public async Task<string?> Decrypt(string? text)
    {
        encryptionHelper ??= GetEncryptionHelper();
        if (text == null)
            return null;
        return encryptionHelper.DecryptString(text);
    }

    public async Task<string?> Encrypt(string? text)
    {
        encryptionHelper ??= GetEncryptionHelper();
        if (text == null)
            return null;
        return encryptionHelper.EncryptString(text);
    }

    public EncryptionHelper GetEncryptionHelper() =>
        new(Encoding.UTF8.GetBytes(GetPassword()), Convert.FromBase64String(cred.LoginSalt));

    public string GetPassword()
    {
        var credentials = AdysTech.CredentialManager.CredentialManager.GetCredentials($"{cred.PackageName}-{cred.Account}");
        if (credentials != null)
            return credentials.Password;

        try
        {
            AdysTech.CredentialManager.CredentialManager.RemoveCredentials($"{cred.PackageName}-{cred.Account}");
        }
        catch (Exception)
        {
            // ignored
        }

        var password = EncryptionHelper.GetRandomHexString(128);
        AdysTech.CredentialManager.CredentialManager.SaveCredentials
        (
            $"{cred.PackageName}-{cred.Account}",
            new NetworkCredential
            {
                UserName = cred.Account,
                Password = password
            }
        );
        return password;
    }

    public async Task<bool> IsSupported() =>
        true;

    public async Task Unregister() =>
        AdysTech.CredentialManager.CredentialManager.RemoveCredentials($"{cred.PackageName}-{cred.Account}");
}
