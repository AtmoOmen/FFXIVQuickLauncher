using System.Net;
using Serilog;

namespace XIVLauncher.Common.Http;

/// <summary>
///     代理配置快照, 由应用设置层提供, 避免公共库依赖 UI 层类型
/// </summary>
public sealed record ProxyConfigSnapshot
(
    string  Type,
    string  Host,
    int     Port,
    string? Username = null,
    string? Password = null
)
{
    public bool IsDisabled =>
        string.IsNullOrWhiteSpace(Host)
        || Port is < 1 or > 65535
        || string.IsNullOrWhiteSpace(Type)
        || string.Equals(Type, "None", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     启动器全局代理提供者。
///     HTTP/HTTPS 与 SOCKS4/4a/5 均通过 SocketsHttpHandler 原生支持, 无需额外依赖。
///     注意: .NET 的 SOCKS5 实现为本地 DNS 解析 (非 socks5h 远程解析)。
/// </summary>
public static class XLProxyProvider
{
    public static IWebProxy? Current { get; private set; }

    public static void Apply(ProxyConfigSnapshot? config)
    {
        var webProxy = BuildWebProxy(config);
        Current      = webProxy is null ? null : new SdoScopedProxy(webProxy);

        Log.Information
        (
            "[XLProxyProvider] 代理配置已应用 (仅对盛趣域名 *.sdo.com / *.jijiagames.com 生效): {Proxy}",
            webProxy is WebProxy { Address: not null } proxy ? proxy.Address.ToString() : "无"
        );
    }

    public static IWebProxy? BuildWebProxy(ProxyConfigSnapshot? config)
    {
        if (config is null || config.IsDisabled)
            return null;

        try
        {
            var scheme = config.Type.Trim().ToLowerInvariant() switch
            {
                "http"   => "http",
                "https"  => "https",
                "socks5" => "socks5",
                _        => null
            };

            if (scheme == null)
            {
                Log.Warning("[XLProxyProvider] 不支持的代理类型: {ProxyType}", config.Type);
                return null;
            }

            var proxy = new WebProxy($"{scheme}://{config.Host.Trim()}:{config.Port}");

            if (!string.IsNullOrWhiteSpace(config.Username))
                proxy.Credentials = new NetworkCredential(config.Username, config.Password);

            return proxy;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[XLProxyProvider] 构建代理配置失败: {ProxyHost}:{ProxyPort} ({ProxyType})", config.Host, config.Port, config.Type);
            return null;
        }
    }

    /// <summary>
    ///     仅对盛趣域名 (*.sdo.com / *.jijiagames.com) 生效的代理包装:
    ///     其余目标一律直连, 避免 GitHub / Dalamud / NuGet 等流量被错误导入代理。
    /// </summary>
    private sealed class SdoScopedProxy(IWebProxy inner) : IWebProxy
    {
        public ICredentials? Credentials
        {
            get => inner.Credentials;
            set => inner.Credentials = value;
        }

        public Uri? GetProxy(Uri destination) =>
            IsSdoHost(destination.Host) ? inner.GetProxy(destination) : destination;

        public bool IsBypassed(Uri host) =>
            !IsSdoHost(host.Host);

        private static bool IsSdoHost(string host) =>
            string.Equals(host, "sdo.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".sdo.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "jijiagames.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".jijiagames.com", StringComparison.OrdinalIgnoreCase);
    }
}
