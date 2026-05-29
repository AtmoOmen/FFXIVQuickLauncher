using System;
using System.Security.Cryptography;
using System.Text;
using XIVLauncher.GamePatchV3;
using Xunit;

namespace XIVLauncher.Test.GamePatchV3;

public sealed class FileKeyTests
{
    private const string AppId   = "100001900";
    private const string Version = "0.0.0.20";

    private static string ExpectedKey(string appId, string version, string filePath) =>
        Convert.ToHexString(MD5.HashData(Encoding.Unicode.GetBytes($"{appId}_{version}_{filePath}")));

    [Fact]
    public void GetFileKey_MatchesIndependentMd5()
    {
        const string filePath = "game/sqpack/ex1/020101.win32.index";

        var key = GameFileDownloader.GetFileKey(AppId, Version, filePath);

        Assert.Equal(ExpectedKey(AppId, Version, filePath), key);
    }

    [Fact]
    public void GetFileKey_IsDeterministic()
    {
        const string filePath = "game/ffxivgame.ver";

        Assert.Equal
        (
            GameFileDownloader.GetFileKey(AppId, Version, filePath),
            GameFileDownloader.GetFileKey(AppId, Version, filePath)
        );
    }

    [Fact]
    public void GetFileKey_LeadingBackslashChangesKey()
    {
        // 记录这次 404 根因: 带前导反斜杠的路径与不带的会算出不同 key
        // 真实 CDN 期望无前导反斜杠, 修复路径用的正是无前导形式
        const string withoutBackslash = "game/sqpack/file.dat";
        const string withBackslash    = "\\game\\sqpack\\file.dat";

        Assert.NotEqual
        (
            GameFileDownloader.GetFileKey(AppId, Version, withoutBackslash),
            GameFileDownloader.GetFileKey(AppId, Version, withBackslash)
        );
    }

    [Fact]
    public void GetFileKey_DifferentVersionChangesKey()
    {
        const string filePath = "game/sqpack/file.dat";

        Assert.NotEqual
        (
            GameFileDownloader.GetFileKey(AppId, "0.0.0.19", filePath),
            GameFileDownloader.GetFileKey(AppId, "0.0.0.20", filePath)
        );
    }

    [Fact]
    public void CdnLinkSigner_Sign_PreservesSchemeHostAndPathTail()
    {
        var original = new Uri("https://ff14.jijiagames.com/v3client/build/file.dat");

        var signed = CDNLinkSigner.Sign(original);

        Assert.Equal("https", signed.Scheme);
        Assert.Equal("ff14.jijiagames.com", signed.Host);
        // 签名形如 /{cdnKey}/{timeStampHex}{absolutePath}, 末尾保留原始路径
        Assert.EndsWith("/v3client/build/file.dat", signed.AbsolutePath);
    }
}
