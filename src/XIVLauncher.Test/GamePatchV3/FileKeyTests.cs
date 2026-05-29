using System;
using System.Security.Cryptography;
using System.Text;
using XIVLauncher.GamePatchV3;
using Xunit;

namespace XIVLauncher.Test.GamePatchV3;

public sealed class FileKeyTests
{
    private const string GAME_ID = "100001900";
    private const string VERSION = "0.0.0.20";

    private static string ExpectedKey(string appId, string version, string filePath) =>
        Convert.ToHexString(MD5.HashData(Encoding.Unicode.GetBytes($"{appId}_{version}_{filePath}")));

    [Fact]
    public void GetFileKey_MatchesIndependentMd5()
    {
        const string FILE_PATH = "game/sqpack/ex1/020101.win32.index";

        var key = GameFileDownloader.GetFileKey(GAME_ID, VERSION, FILE_PATH);

        Assert.Equal(ExpectedKey(GAME_ID, VERSION, FILE_PATH), key);
    }

    [Fact]
    public void GetFileKey_IsDeterministic()
    {
        const string FILE_PATH = "game/ffxivgame.ver";

        Assert.Equal
        (
            GameFileDownloader.GetFileKey(GAME_ID, VERSION, FILE_PATH),
            GameFileDownloader.GetFileKey(GAME_ID, VERSION, FILE_PATH)
        );
    }

    [Fact]
    public void GetFileKey_LeadingBackslashChangesKey()
    {
        // 记录这次 404 根因: 带前导反斜杠的路径与不带的会算出不同 key
        // 真实 CDN 期望无前导反斜杠, 修复路径用的正是无前导形式
        const string WITHOUT_BACKSLASH = "game/sqpack/file.dat";
        const string WITH_BACKSLASH    = "\\game\\sqpack\\file.dat";

        Assert.NotEqual
        (
            GameFileDownloader.GetFileKey(GAME_ID, VERSION, WITHOUT_BACKSLASH),
            GameFileDownloader.GetFileKey(GAME_ID, VERSION, WITH_BACKSLASH)
        );
    }

    [Fact]
    public void GetFileKey_DifferentVersionChangesKey()
    {
        const string FILE_PATH = "game/sqpack/file.dat";

        Assert.NotEqual
        (
            GameFileDownloader.GetFileKey(GAME_ID, "0.0.0.19", FILE_PATH),
            GameFileDownloader.GetFileKey(GAME_ID, "0.0.0.20", FILE_PATH)
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
