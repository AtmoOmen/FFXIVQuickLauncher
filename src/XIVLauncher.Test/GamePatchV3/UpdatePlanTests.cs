using System.Collections.Generic;
using System.IO;
using XIVLauncher.GamePatchV3;
using XIVLauncher.GamePatchV3.Models;
using Xunit;

namespace XIVLauncher.Test.GamePatchV3;

public sealed class UpdatePlanTests
{
    private const string CurrentGameVersion = "2026.03.17.0000.0000";
    private const string TargetGameVersion  = "2026.05.01.0000.0000";

    // 构造一个含"当前状态标记包"(VersionView 映射游戏版本 -> 数据版本)与若干补丁包的远端版本信息
    private static RemoteVersion BuildRemote(string must, IEnumerable<GameVersionPackage> packages, string min = "0.0.0.0") =>
        new()
        {
            BaseUrl  = "https://example.com/base",
            Areas    = [new GameVersionArea { Id = "0", Must = must, Min = min, View = TargetGameVersion }],
            Packages = [..packages]
        };

    private static GameVersionPackage Marker(string toDataVersion, string gameVersion) =>
        new() { VersionView = gameVersion, From = "0.0.0.0", To = toDataVersion };

    private static GameVersionPackage Patch(string name, string from, string to, string versionView = "") =>
        new() { Name = name, From = from, To = to, VersionView = versionView, FileListUrl = $"/{name}/list.dat" };

    [Fact]
    public void BuildPlan_MultiHop_SelectsOrderedPackages()
    {
        var remote = BuildRemote
        (
            "0.0.0.20",
            [
                Marker("0.0.0.16", CurrentGameVersion),
                Patch("patch-16-19", "0.0.0.16", "0.0.0.19"),
                Patch("patch-19-20", "0.0.0.19", "0.0.0.20", TargetGameVersion)
            ]
        );

        var plan = GamePatchMetadataClient.BuildPlanFromRemoteVersion(remote, CurrentGameVersion, false);

        Assert.NotNull(plan);
        Assert.Equal("0.0.0.16", plan.CurrentDataVersion);
        Assert.Equal("0.0.0.20", plan.TargetDataVersion);
        Assert.Equal(TargetGameVersion, plan.TargetGameVersion);
        Assert.Equal(2, plan.Packages.Count);
        Assert.Equal("patch-16-19", plan.Packages[0].Name);
        Assert.Equal("patch-19-20", plan.Packages[1].Name);
    }

    [Fact]
    public void BuildPlan_AlreadyAtTarget_ReturnsNull()
    {
        var remote = BuildRemote
        (
            "0.0.0.20",
            [Marker("0.0.0.20", CurrentGameVersion)]
        );

        var plan = GamePatchMetadataClient.BuildPlanFromRemoteVersion(remote, CurrentGameVersion, false);

        Assert.Null(plan);
    }

    [Fact]
    public void BuildPlan_AlreadyAtTargetButForced_StillBuildsWhenPackageExists()
    {
        var remote = BuildRemote
        (
            "0.0.0.20",
            [
                Marker("0.0.0.20", CurrentGameVersion),
                Patch("patch-20-20", "0.0.0.20", "0.0.0.20")
            ]
        );

        // forceUpdate=true 时跳过"已是目标"短路, 但 cursor==Must 立即结束 -> 0 包计划
        var plan = GamePatchMetadataClient.BuildPlanFromRemoteVersion(remote, CurrentGameVersion, true);

        Assert.NotNull(plan);
        Assert.Empty(plan.Packages);
    }

    [Fact]
    public void BuildPlan_CycleInChain_Throws()
    {
        var remote = BuildRemote
        (
            "0.0.0.20",
            [
                Marker("0.0.0.16", CurrentGameVersion),
                Patch("patch-16-19", "0.0.0.16", "0.0.0.19"),
                Patch("patch-19-16", "0.0.0.19", "0.0.0.16")
            ]
        );

        Assert.Throws<InvalidDataException>(() => GamePatchMetadataClient.BuildPlanFromRemoteVersion(remote, CurrentGameVersion, false));
    }

    [Fact]
    public void BuildPlan_MissingNextHop_Throws()
    {
        var remote = BuildRemote
        (
            "0.0.0.20",
            [
                Marker("0.0.0.16", CurrentGameVersion),
                Patch("patch-16-19", "0.0.0.16", "0.0.0.19")
            ]
        );

        Assert.Throws<InvalidDataException>(() => GamePatchMetadataClient.BuildPlanFromRemoteVersion(remote, CurrentGameVersion, false));
    }

    [Fact]
    public void BuildPlan_UnrecognizedGameVersion_ThrowsUnsupported()
    {
        var remote = BuildRemote
        (
            "0.0.0.20",
            [Patch("patch-16-19", "0.0.0.16", "0.0.0.19")]
        );

        // 当前游戏版本匹配不到任何包的 VersionView -> 数据版本为空 -> 不受支持
        Assert.Throws<UnsupportedGameVersionException>(() => GamePatchMetadataClient.BuildPlanFromRemoteVersion(remote, "1999.01.01.0000.0000", false));
    }

    [Fact]
    public void BuildPlan_VersionBelowMinimum_ThrowsUnsupported()
    {
        var remote = BuildRemote
        (
            "0.0.0.20",
            [
                Marker("0.0.0.16", CurrentGameVersion),
                Patch("patch-16-19", "0.0.0.16", "0.0.0.19"),
                Patch("patch-19-20", "0.0.0.19", "0.0.0.20")
            ],
            min: "0.0.0.18"
        );

        Assert.Throws<UnsupportedGameVersionException>(() => GamePatchMetadataClient.BuildPlanFromRemoteVersion(remote, CurrentGameVersion, false));
    }

    [Fact]
    public void BuildPlan_SingleHop_OnePackage()
    {
        var remote = BuildRemote
        (
            "0.0.0.20",
            [
                Marker("0.0.0.19", CurrentGameVersion),
                Patch("patch-19-20", "0.0.0.19", "0.0.0.20", TargetGameVersion)
            ]
        );

        var plan = GamePatchMetadataClient.BuildPlanFromRemoteVersion(remote, CurrentGameVersion, false);

        Assert.NotNull(plan);
        Assert.Single(plan.Packages);
        Assert.Equal("0.0.0.19", plan.CurrentDataVersion);
    }
}
