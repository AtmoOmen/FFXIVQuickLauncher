using System.Net;
using Serilog;

namespace XIVLauncher.Common.Http;

/// <summary>
///     启动器全局代理提供者, 由启动流程根据代理配置构建一次。
///     HTTP/HTTPS/SOCKS5 由 SocketsHttpHandler 原生支持。
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
