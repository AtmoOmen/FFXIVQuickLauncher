using System.Threading.Tasks;

namespace XIVLauncher.Accounts.Cred.CredProviders;

internal class NoCred : ICredProvider
{
    public NoCred(CredData cred)
    {
    }

    public string GetName() => "无加密";

    public string GetDescription() => "";

    public async Task ClearCache()
    {
    }

    public async Task<string?> Decrypt(string? text) =>
        text;

    public async Task<string?> Encrypt(string? text) =>
        text;

    public async Task<bool> IsSupported() =>
        true;

    public async Task Unregister()
    {

    }
}
