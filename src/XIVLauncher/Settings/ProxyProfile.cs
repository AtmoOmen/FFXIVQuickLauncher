using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using XIVLauncher.Common.Http;

namespace XIVLauncher.Settings;

/// <summary>
///     单个代理配置条目 (预设)
/// </summary>
public sealed class ProxyProfile
{
    private static readonly byte[] PasswordEntropy = "XIVLauncherCN.ProxyPassword"u8.ToArray();

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public ProxyType ProxyType { get; set; }

    public string ProxyHost { get; set; } = string.Empty;

    public int ProxyPort { get; set; }

    public string ProxyUsername { get; set; } = string.Empty;

    /// <summary>
    ///     DPAPI (CurrentUser) 加密后的密码, Base64;空表示无密码
    /// </summary>
    public string ProxyPasswordEncrypted { get; set; } = string.Empty;

    /// <summary>
    ///     明文密码, 仅驻留内存, 不序列化
    /// </summary>
    [JsonIgnore]
    public string? ProxyPasswordPlain { get; set; }

    [JsonIgnore]
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? "未命名配置" : Name;

    public void SetPassword(string? plainPassword)
    {
        if (string.IsNullOrEmpty(plainPassword))
        {
            ProxyPasswordEncrypted = string.Empty;
            ProxyPasswordPlain     = null;
            return;
        }

        ProxyPasswordPlain     = plainPassword;
        ProxyPasswordEncrypted = EncryptPassword(plainPassword);
    }

    public string? GetPassword()
    {
        if (ProxyPasswordPlain != null)
            return ProxyPasswordPlain;

        if (string.IsNullOrWhiteSpace(ProxyPasswordEncrypted))
            return null;

        try
        {
            ProxyPasswordPlain = DecryptPassword(ProxyPasswordEncrypted);
            return ProxyPasswordPlain;
        }
        catch
        {
            ProxyPasswordEncrypted = string.Empty;
            return null;
        }
    }

    public ProxyConfigSnapshot? ToSnapshot()
    {
        if (ProxyType == ProxyType.None)
            return null;

        var host = ProxyHost.Trim();
        if (string.IsNullOrWhiteSpace(host) || ProxyPort is < 1 or > 65535)
            return null;

        var username = ProxyUsername.Trim();
        var password = GetPassword();

        return new ProxyConfigSnapshot
        (
            ProxyType.ToString(),
            host,
            ProxyPort,
            string.IsNullOrWhiteSpace(username) ? null : username,
            string.IsNullOrEmpty(password) ? null : password
        );
    }

    private static string EncryptPassword(string plainPassword) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plainPassword), PasswordEntropy, DataProtectionScope.CurrentUser));

    private static string DecryptPassword(string encryptedPassword) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encryptedPassword), PasswordEntropy, DataProtectionScope.CurrentUser));
}
