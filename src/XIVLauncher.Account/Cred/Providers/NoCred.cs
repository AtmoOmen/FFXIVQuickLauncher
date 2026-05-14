namespace XIVLauncher.Account.Cred.Providers;

internal class NoCred : ICredProvider
{
    public string GetName() => "无加密";

    public string GetDescription() => string.Empty;

    public async Task ClearCache() { }

    public async Task<string?> Decrypt(string? text) =>
        text;

    public async Task<string?> Encrypt(string? text) =>
        text;

    public async Task<bool> IsSupported() =>
        true;

    public async Task Unregister() { }
}
