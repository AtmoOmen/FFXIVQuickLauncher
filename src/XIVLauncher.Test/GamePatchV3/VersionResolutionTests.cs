using XIVLauncher.GamePatchV3;
using Xunit;

namespace XIVLauncher.Test.GamePatchV3;

public sealed class VersionResolutionTests
{
    [Theory]
    [InlineData("0.0.0.20", "0.0.0.16", 1)]
    [InlineData("0.0.0.16", "0.0.0.20", -1)]
    [InlineData("0.0.0.16", "0.0.0.16", 0)]
    [InlineData("0.0.1.0", "0.0.0.99", 1)]
    [InlineData("1.0.0.0", "0.9.9.9", 1)]
    [InlineData("0.0.0.7", "0.0.0.7", 0)]
    public void CompareDataVersions_OrdersNumerically(string left, string right, int expectedSign) =>
        Assert.Equal(expectedSign, System.Math.Sign(GamePatchMetadataClient.CompareDataVersions(left, right)));

    [Fact]
    public void CompareDataVersions_DifferentPartCount_PadsWithZero()
    {
        // "0.0.0" 视作 "0.0.0.0", 应小于 "0.0.0.1"
        Assert.True(GamePatchMetadataClient.CompareDataVersions("0.0.0", "0.0.0.1") < 0);
        Assert.Equal(0, GamePatchMetadataClient.CompareDataVersions("0.0.0", "0.0.0.0"));
    }

    [Theory]
    [InlineData("0.0.0.20", "0.0.0.7", true)]
    [InlineData("0.0.0.7", "0.0.0.7", true)]
    [InlineData("0.0.0.6", "0.0.0.7", false)]
    [InlineData("", "0.0.0.7", false)]
    [InlineData("   ", "0.0.0.7", false)]
    public void IsSupportedDataVersion_ChecksMinimum(string dataVersion, string minimum, bool expected) =>
        Assert.Equal(expected, GamePatchMetadataClient.IsSupportedDataVersion(dataVersion, minimum));
}
