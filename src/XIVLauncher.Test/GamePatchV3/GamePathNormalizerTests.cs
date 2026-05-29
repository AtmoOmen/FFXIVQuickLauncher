using XIVLauncher.GamePatchV3;
using Xunit;

namespace XIVLauncher.Test.GamePatchV3;

public sealed class GamePathNormalizerTests
{
    [Theory]
    [InlineData("game/sqpack/ex1/020101.win32.index", true, "game/sqpack/ex1/020101.win32.index")]
    [InlineData("game\\sqpack\\ex1\\020101.win32.index", true, "game/sqpack/ex1/020101.win32.index")]
    [InlineData("GAME/sqpack/test.dat", true, "GAME/sqpack/test.dat")]
    [InlineData("boot/ffxivboot.exe", false, "")]
    [InlineData("", false, "")]
    [InlineData("   ", false, "")]
    public void TryNormalizeGameRelativePath_ResolvesPrefixAndSeparators(string input, bool expectedResult, string expectedPath)
    {
        var result = GamePathNormalizer.TryNormalizeGameRelativePath(input, out var normalized);

        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedPath, normalized);
    }

    [Theory]
    [InlineData("game/sqpack/../sqpack/ex1/file.dat", "game/sqpack/ex1/file.dat")]
    [InlineData("game/./sqpack/./file.dat", "game/sqpack/file.dat")]
    [InlineData("game/a/b/../../sqpack/file.dat", "game/sqpack/file.dat")]
    public void TryNormalizeGameRelativePath_CollapsesTraversal(string input, string expectedPath)
    {
        var result = GamePathNormalizer.TryNormalizeGameRelativePath(input, out var normalized);

        Assert.True(result);
        Assert.Equal(expectedPath, normalized);
    }

    [Fact]
    public void TryNormalizeGameRelativePath_TraversalEscapingGamePrefix_Rejected()
    {
        // game/../boot/x 归一后为 boot/x, 不再以 game/ 开头, 应拒绝
        var result = GamePathNormalizer.TryNormalizeGameRelativePath("game/../boot/x.dat", out var normalized);

        Assert.False(result);
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData("game/sqpack/ex1/file.dat", "\\game\\sqpack\\ex1\\file.dat")]
    [InlineData("game/file.dat", "\\game\\file.dat")]
    public void ToCanonicalSdoPathFromGameRelativePath_PrependsBackslash(string input, string expected) =>
        Assert.Equal(expected, GamePathNormalizer.ToCanonicalSdoPathFromGameRelativePath(input));

    [Theory]
    [InlineData("game/sqpack/file.dat", "\\game\\sqpack\\file.dat")]
    [InlineData("\\game\\sqpack\\file.dat", "\\game\\sqpack\\file.dat")]
    [InlineData("game\\sqpack\\file.dat", "\\game\\sqpack\\file.dat")]
    public void NormalizeDownloadPath_BackslashWithLeadingSeparator(string input, string expected) =>
        Assert.Equal(expected, GamePathNormalizer.NormalizeDownloadPath(input));

    [Fact]
    public void CombineWithRootPath_JoinsWithPlatformSeparator()
    {
        var combined = GamePathNormalizer.CombineWithRootPath("C:\\Game", "game/sqpack/file.dat");

        Assert.Contains("Game", combined);
        Assert.EndsWith("file.dat", combined);
        Assert.DoesNotContain("/", combined);
    }
}
