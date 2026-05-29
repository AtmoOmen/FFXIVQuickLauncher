using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using XIVLauncher.GamePatchV3;
using XIVLauncher.GamePatchV3.Integrity.Models;
using Xunit;

namespace XIVLauncher.Test.GamePatchV3;

public sealed class FileDownloaderVerifyTests : IDisposable
{
    private readonly string root;

    public FileDownloaderVerifyTests()
    {
        root = Path.Combine(Path.GetTempPath(), "xltest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
    }

    private (string Md5, long Size) WriteGameFile(string gameRelative, byte[] content)
    {
        var full = Path.Combine(root, gameRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return (Convert.ToHexString(MD5.HashData(content)), content.Length);
    }

    private static IntegrityPathEntry Entry(string gameRelative, string md5, ulong size) =>
        new
        (
            0,
            GamePathNormalizer.NormalizeDownloadPath(gameRelative),
            GamePathNormalizer.ToCanonicalSdoPathFromGameRelativePath(gameRelative),
            gameRelative,
            gameRelative["game/".Length..],
            md5,
            size
        );

    [Fact]
    public async Task VerifyFiles_GoodFile_NotBroken()
    {
        var content = "hello game file"u8.ToArray();
        var (md5, size) = WriteGameFile("game/sqpack/good.dat", content);

        using var downloader = new GameFileDownloader();
        downloader.Construct([Entry("game/sqpack/good.dat", md5, (ulong)size)], "https://example.com", "0.0.0.20");
        await downloader.VerifyFiles(root, false, 1);

        Assert.Empty(downloader.GetBrokenFiles());
    }

    [Fact]
    public async Task VerifyFiles_WrongHash_Broken()
    {
        var (_, size) = WriteGameFile("game/sqpack/bad.dat", "actual content"u8.ToArray());

        using var downloader = new GameFileDownloader();
        downloader.Construct([Entry("game/sqpack/bad.dat", "DEADBEEFDEADBEEFDEADBEEFDEADBEEF", (ulong)size)], "https://example.com", "0.0.0.20");
        await downloader.VerifyFiles(root, false, 1);

        var broken = downloader.GetBrokenFiles();
        Assert.Equal("\\game\\sqpack\\bad.dat", Assert.Single(broken));
    }

    [Fact]
    public async Task VerifyFiles_WrongSize_Broken()
    {
        var content = "12345"u8.ToArray();
        var (md5, _) = WriteGameFile("game/sqpack/size.dat", content);

        using var downloader = new GameFileDownloader();
        // 尺寸声明与实际不符, 即便 MD5 文本正确也应判坏(size 优先短路)
        downloader.Construct([Entry("game/sqpack/size.dat", md5, 9999UL)], "https://example.com", "0.0.0.20");
        await downloader.VerifyFiles(root, false, 1);

        Assert.Single(downloader.GetBrokenFiles());
    }

    [Fact]
    public async Task VerifyFiles_MissingFile_Broken()
    {
        using var downloader = new GameFileDownloader();
        downloader.Construct([Entry("game/sqpack/missing.dat", "AA", 10UL)], "https://example.com", "0.0.0.20");
        await downloader.VerifyFiles(root, false, 1);

        Assert.Single(downloader.GetBrokenFiles());
    }
}
