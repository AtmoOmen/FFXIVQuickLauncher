using XIVLauncher.GamePatchV3.Integrity.Models;
using Xunit;

namespace XIVLauncher.Test.GamePatchV3;

public sealed class IntegrityPathEntryTests
{
    [Fact]
    public void BuildEntries_NormalizesAndCarriesHashSize()
    {
        var remote = new IntegrityCheckResult
        {
            Hashes = { ["\\game\\sqpack\\ex1\\file.dat"] = "ABCDEF" },
            Sizes  = { ["\\game\\sqpack\\ex1\\file.dat"] = 1234UL }
        };

        var entries = IntegrityPathEntry.BuildEntries(remote);

        var entry = Assert.Single(entries);
        Assert.Equal("game/sqpack/ex1/file.dat", entry.GameRelativePath);
        Assert.Equal("sqpack/ex1/file.dat", entry.LocalRelativePath);
        Assert.Equal("\\game\\sqpack\\ex1\\file.dat", entry.CanonicalSdoPath);
        Assert.Equal("ABCDEF", entry.Hash);
        Assert.Equal(1234UL, entry.Size);
    }

    [Fact]
    public void BuildEntries_SkipsNonGameAndEmptyKeys()
    {
        var remote = new IntegrityCheckResult
        {
            Hashes =
            {
                ["\\game\\file.dat"]      = "AA",
                ["\\boot\\ffxivboot.exe"] = "BB",
                [""]                      = "CC"
            }
        };

        var entries = IntegrityPathEntry.BuildEntries(remote);

        var entry = Assert.Single(entries);
        Assert.Equal("game/file.dat", entry.GameRelativePath);
    }

    [Fact]
    public void BuildEntries_DeduplicatesByCanonicalPath_PrefersCleanDownloadPath()
    {
        // 两个键归一后指向同一 canonical 路径: 一个"干净"(DownloadPath==canonical), 一个含冗余段
        var remote = new IntegrityCheckResult
        {
            Hashes =
            {
                ["\\game\\sqpack\\file.dat"]    = "CLEAN",
                ["\\game\\sqpack\\.\\file.dat"] = "MESSY"
            }
        };

        var entries = IntegrityPathEntry.BuildEntries(remote);

        var entry = Assert.Single(entries);
        Assert.Equal("\\game\\sqpack\\file.dat", entry.CanonicalSdoPath);
        // 干净条目的 DownloadPath 与 canonical 相等, 应被优先保留
        Assert.Equal(entry.CanonicalSdoPath, entry.DownloadPath);
        Assert.Equal("CLEAN", entry.Hash);
    }
}
