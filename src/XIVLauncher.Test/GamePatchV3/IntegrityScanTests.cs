using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using XIVLauncher.Common;
using XIVLauncher.GamePatchV3;
using XIVLauncher.GamePatchV3.Integrity;
using XIVLauncher.GamePatchV3.Integrity.Models;
using Xunit;

namespace XIVLauncher.Test.GamePatchV3;

public sealed class IntegrityScanTests : IDisposable
{
    private readonly DirectoryInfo gameDir;

    public IntegrityScanTests()
    {
        gameDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "xlscan_" + Guid.NewGuid().ToString("N")));
        gameDir.Create();
    }

    public void Dispose()
    {
        try
        {
            gameDir.Delete(true);
        }
        catch (IOException)
        {
        }
    }

    private (string Md5, ulong Size) Write(string gameRelative, byte[] content)
    {
        var full = Path.Combine(gameDir.FullName, gameRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return (Convert.ToHexString(MD5.HashData(content)), (ulong)content.Length);
    }

    [Fact]
    public async Task ScanThenCompare_MatchingFiles_Valid()
    {
        var a = Write("game/sqpack/a.dat", "alpha"u8.ToArray());
        var b = Write("game/sqpack/b.dat", "bravo"u8.ToArray());

        var remote = new IntegrityCheckResult
        {
            Hashes = { ["\\game\\sqpack\\a.dat"] = a.Md5, ["\\game\\sqpack\\b.dat"] = b.Md5 },
            Sizes  = { ["\\game\\sqpack\\a.dat"] = a.Size, ["\\game\\sqpack\\b.dat"] = b.Size }
        };

        var local   = await GameIntegrityChecker.RunIntegrityCheckAsync(gameDir, null);
        var outcome = GameIntegrityChecker.CompareIntegrity(remote, local);

        Assert.Equal(IntegrityCheckCompareResult.Valid, outcome.CompareResult);
    }

    [Fact]
    public async Task ScanThenCompare_TamperedFile_DetectedAsMismatch()
    {
        var a = Write("game/sqpack/a.dat", "alpha"u8.ToArray());
        Write("game/sqpack/b.dat", "tampered"u8.ToArray());

        // 远端期望 b.dat 是 "bravo", 但本地被改成 "tampered"
        var remote = new IntegrityCheckResult
        {
            Hashes =
            {
                ["\\game\\sqpack\\a.dat"] = a.Md5,
                ["\\game\\sqpack\\b.dat"] = Convert.ToHexString(MD5.HashData("bravo"u8.ToArray()))
            },
            Sizes =
            {
                ["\\game\\sqpack\\a.dat"] = a.Size,
                ["\\game\\sqpack\\b.dat"] = 5UL
            }
        };

        var local   = await GameIntegrityChecker.RunIntegrityCheckAsync(gameDir, null);
        var outcome = GameIntegrityChecker.CompareIntegrity(remote, local);

        Assert.Equal(IntegrityCheckCompareResult.Invalid, outcome.CompareResult);
        Assert.Contains("\\game\\sqpack\\b.dat", outcome.Report);
    }

    [Fact]
    public void GetVer_ReadsWrittenVersion()
    {
        // 验证修复/安装写回的版本文件能被正确读取(链路里 GetVer 是版本判定的起点)
        Repository.Ffxiv.SetVer(gameDir, "2026.05.01.0000.0000");

        Assert.Equal("2026.05.01.0000.0000", Repository.Ffxiv.GetVer(gameDir).Trim());
    }
}
