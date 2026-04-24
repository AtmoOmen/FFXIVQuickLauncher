using System.Threading.Tasks;

namespace XIVLauncher.Accounts.Cred;

public interface ICredProvider
{
    string GetName();

    string GetDescription();

    Task<bool> IsSupported();

    Task ClearCache();

    Task Unregister();

    Task<string?> Encrypt(string? text);

    Task<string?> Decrypt(string? text);
}
