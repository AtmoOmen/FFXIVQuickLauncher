using System.Net;
using XIVLauncher.Common.Http;
using Xunit;

namespace XIVLauncher.Test.Http;

public sealed class XLProxyProviderTests
{
    [Fact]
    public void BuildWebProxy_NullOrNone_ReturnsNull()
    {
        Assert.Null(XLProxyProvider.BuildWebProxy(null));
        Assert.Null(XLProxyProvider.BuildWebProxy(new ProxyConfigSnapshot("None", "127.0.0.1", 1080)));
    }

    [Fact]
    public void BuildWebProxy_Http_ReturnsProxyWithoutCredentials()
    {
        var proxy = XLProxyProvider.BuildWebProxy(new ProxyConfigSnapshot("Http", "127.0.0.1", 8080));

        var webProxy = Assert.IsType<WebProxy>(proxy);
        Assert.Equal("http://127.0.0.1:8080/", webProxy.Address!.ToString());
        Assert.Null(webProxy.Credentials);
    }

    [Fact]
    public void BuildWebProxy_Https_ReturnsHttpsAddress()
    {
        var proxy = XLProxyProvider.BuildWebProxy(new ProxyConfigSnapshot("Https", "proxy.example.com", 443));

        var webProxy = Assert.IsType<WebProxy>(proxy);
        // Uri 会归一化默认端口
        Assert.Equal("https://proxy.example.com/", webProxy.Address!.ToString());
    }

    [Fact]
    public void BuildWebProxy_Socks5_ReturnsSocksAddress()
    {
        var proxy = XLProxyProvider.BuildWebProxy(new ProxyConfigSnapshot("Socks5", "127.0.0.1", 1080));

        var webProxy = Assert.IsType<WebProxy>(proxy);
        Assert.Equal("socks5://127.0.0.1:1080/", webProxy.Address!.ToString());
    }

    [Fact]
    public void BuildWebProxy_WithCredentials_SetsNetworkCredential()
    {
        var proxy = XLProxyProvider.BuildWebProxy
        (
            new ProxyConfigSnapshot("Socks5", "127.0.0.1", 1080, "user", "pass")
        );

        var webProxy    = Assert.IsType<WebProxy>(proxy);
        var credentials = Assert.IsType<NetworkCredential>(webProxy.Credentials);
        Assert.Equal("user", credentials.UserName);
        Assert.Equal("pass", credentials.Password);
    }

    [Fact]
    public void BuildWebProxy_WithoutUsername_IgnoresPassword()
    {
        var proxy = XLProxyProvider.BuildWebProxy
        (
            new ProxyConfigSnapshot("Http", "127.0.0.1", 8080, null, "pass")
        );

        var webProxy = Assert.IsType<WebProxy>(proxy);
        Assert.Null(webProxy.Credentials);
    }

    [Theory]
    [InlineData("Socks5", "", 1080)]
    [InlineData("Socks5", "127.0.0.1", 0)]
    [InlineData("Socks5", "127.0.0.1", 65536)]
    [InlineData("Socks5", "127.0.0.1", -1)]
    [InlineData("Ftp", "127.0.0.1", 1080)]
    [InlineData("", "127.0.0.1", 1080)]
    public void BuildWebProxy_InvalidInput_ReturnsNull(string type, string host, int port)
    {
        var proxy = XLProxyProvider.BuildWebProxy(new ProxyConfigSnapshot(type, host, port));

        Assert.Null(proxy);
    }

    [Fact]
    public void Apply_Current_OnlyProxiesSdoDomains()
    {
        XLProxyProvider.Apply(new ProxyConfigSnapshot("Http", "127.0.0.1", 8080));

        try
        {
            var proxy = XLProxyProvider.Current;
            Assert.NotNull(proxy);

            var sdoUri = new Uri("https://ff.dorado.sdo.com/ff/area/serverlist_new.js");
            Assert.False(proxy.IsBypassed(sdoUri));
            Assert.Equal("http://127.0.0.1:8080/", proxy.GetProxy(sdoUri)!.ToString());

            Assert.False(proxy.IsBypassed(new Uri("https://cas.sdo.com/authen/getGuid.json")));
            Assert.False(proxy.IsBypassed(new Uri("https://n1.cas.sdo.com/authen/ssoLogin.json")));
            Assert.False(proxy.IsBypassed(new Uri("https://ff14bjz.sdo.com/api/orderserivce/pageInit")));
            Assert.False(proxy.IsBypassed(new Uri("https://v3launcher.jijiagames.com/v3launcher/build/ver2data/x")));

            var githubUri = new Uri("https://github.com/AtmoOmen/FFXIVQuickLauncher");
            Assert.True(proxy.IsBypassed(githubUri));
            Assert.Equal(githubUri, proxy.GetProxy(githubUri));

            Assert.True(proxy.IsBypassed(new Uri("https://dalamud-dis.atmoomen.top/RELEASE")));
            Assert.True(proxy.IsBypassed(new Uri("https://api.nuget.org/v3-flatcontainer/x")));
        }
        finally
        {
            XLProxyProvider.Apply(null);
        }
    }

    [Fact]
    public void Apply_Null_ClearsCurrent()
    {
        XLProxyProvider.Apply(null);

        Assert.Null(XLProxyProvider.Current);
    }
}
