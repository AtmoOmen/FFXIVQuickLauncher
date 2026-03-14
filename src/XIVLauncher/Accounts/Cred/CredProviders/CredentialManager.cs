using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace XIVLauncher.Accounts.Cred.CredProviders;

public class CredentialManager : ICredProvider
{
    private const string           ServiceName = "KeyVault";
    private       EncryptionHelper EncryptionHelper;

    private CredData Cred { get; init; }

    public CredentialManager(CredData cred) =>
        Cred = cred;

    public string GetName() => "凭据管理器";

    public string GetDescription() => "使用系统自带的凭据管理器";

    public async Task ClearCache() =>
        EncryptionHelper = null;

    public async Task<string?> Decrypt(string? text)
    {
        EncryptionHelper ??= GetEncryptionHelper();
        if (text == null)
            return null;
        return EncryptionHelper.DecryptString(text);
    }

    public async Task<string?> Encrypt(string? text)
    {
        EncryptionHelper ??= GetEncryptionHelper();
        if (text == null)
            return null;
        return EncryptionHelper.EncryptString(text);
    }

    public EncryptionHelper GetEncryptionHelper() =>
        new(Encoding.UTF8.GetBytes(GetPassword()), Convert.FromBase64String(Cred.LoginSalt));

    public string GetPassword()
    {
        var credentials = AdysTech.CredentialManager.CredentialManager.GetCredentials($"{Cred.PackageName}-{Cred.Account}");
        if (credentials != null)
            return credentials.Password;

        try
        {
            AdysTech.CredentialManager.CredentialManager.RemoveCredentials($"{Cred.PackageName}-{Cred.Account}");
        }
        catch (Exception)
        {
            // ignored
        }

        var password = EncryptionHelper.GetRandomHexString(128);
        AdysTech.CredentialManager.CredentialManager.SaveCredentials
        (
            $"{Cred.PackageName}-{Cred.Account}",
            new NetworkCredential
            {
                UserName = Cred.Account,
                Password = password
            }
        );
        return password;
    }

    public async Task<bool> IsSupported() =>
        true;

    public async Task Unregister() =>
        AdysTech.CredentialManager.CredentialManager.RemoveCredentials($"{Cred.PackageName}-{Cred.Account}");
}
