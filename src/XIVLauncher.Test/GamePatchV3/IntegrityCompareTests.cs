using XIVLauncher.GamePatchV3.Integrity;
using XIVLauncher.GamePatchV3.Integrity.Models;
using Xunit;

namespace XIVLauncher.Test.GamePatchV3;

public sealed class IntegrityCompareTests
{
    private static IntegrityCheckResult Remote() =>
        new()
        {
            Hashes = { ["\\game\\sqpack\\a.dat"] = "AAAA", ["\\game\\sqpack\\b.dat"] = "BBBB" },
            Sizes  = { ["\\game\\sqpack\\a.dat"] = 100UL, ["\\game\\sqpack\\b.dat"] = 200UL }
        };

    [Fact]
    public void Compare_AllMatch_Valid()
    {
        var local = new IntegrityCheckResult
        {
            Hashes = { ["\\game\\sqpack\\a.dat"] = "AAAA", ["\\game\\sqpack\\b.dat"] = "BBBB" },
            Sizes  = { ["\\game\\sqpack\\a.dat"] = 100UL, ["\\game\\sqpack\\b.dat"] = 200UL }
        };

        var outcome = GameIntegrityChecker.CompareIntegrity(Remote(), local);

        Assert.Equal(IntegrityCheckCompareResult.Valid, outcome.CompareResult);
        Assert.Empty(outcome.Report);
    }

    [Fact]
    public void Compare_HashMismatch_Invalid()
    {
        var local = new IntegrityCheckResult
        {
            Hashes = { ["\\game\\sqpack\\a.dat"] = "WRONG", ["\\game\\sqpack\\b.dat"] = "BBBB" },
            Sizes  = { ["\\game\\sqpack\\a.dat"] = 100UL, ["\\game\\sqpack\\b.dat"] = 200UL }
        };

        var outcome = GameIntegrityChecker.CompareIntegrity(Remote(), local);

        Assert.Equal(IntegrityCheckCompareResult.Invalid, outcome.CompareResult);
        Assert.Contains("Mismatch: \\game\\sqpack\\a.dat", outcome.Report);
    }

    [Fact]
    public void Compare_SizeMismatch_ReportedAsSizeMismatch()
    {
        var local = new IntegrityCheckResult
        {
            Hashes = { ["\\game\\sqpack\\a.dat"] = "AAAA", ["\\game\\sqpack\\b.dat"] = "BBBB" },
            Sizes  = { ["\\game\\sqpack\\a.dat"] = 999UL, ["\\game\\sqpack\\b.dat"] = 200UL }
        };

        var outcome = GameIntegrityChecker.CompareIntegrity(Remote(), local);

        Assert.Equal(IntegrityCheckCompareResult.Invalid, outcome.CompareResult);
        Assert.Contains("Size mismatch: \\game\\sqpack\\a.dat", outcome.Report);
    }

    [Fact]
    public void Compare_MissingLocalFile_Invalid()
    {
        var local = new IntegrityCheckResult
        {
            Hashes = { ["\\game\\sqpack\\a.dat"] = "AAAA" },
            Sizes  = { ["\\game\\sqpack\\a.dat"] = 100UL }
        };

        var outcome = GameIntegrityChecker.CompareIntegrity(Remote(), local);

        Assert.Equal(IntegrityCheckCompareResult.Invalid, outcome.CompareResult);
        Assert.Contains("Missing: \\game\\sqpack\\b.dat", outcome.Report);
    }

    [Fact]
    public void Compare_OnlyIndex_IgnoresNonIndexFiles()
    {
        var remote = new IntegrityCheckResult
        {
            Hashes = { ["\\game\\sqpack\\a.win32.index"] = "IDX", ["\\game\\sqpack\\a.win32.dat0"] = "DAT" },
            Sizes  = { ["\\game\\sqpack\\a.win32.index"] = 10UL, ["\\game\\sqpack\\a.win32.dat0"] = 20UL }
        };
        // 本地缺少 .dat0, 但 onlyIndex 模式应只看 .index, 故仍为 Valid
        var local = new IntegrityCheckResult
        {
            Hashes = { ["\\game\\sqpack\\a.win32.index"] = "IDX" },
            Sizes  = { ["\\game\\sqpack\\a.win32.index"] = 10UL }
        };

        var outcome = GameIntegrityChecker.CompareIntegrity(remote, local, onlyIndex: true);

        Assert.Equal(IntegrityCheckCompareResult.Valid, outcome.CompareResult);
    }
}
