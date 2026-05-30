using System.IO;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using XIVLauncher.Common.Constant;
using XIVLauncher.Support;
using Xunit;
using Xunit.Abstractions;

namespace XIVLauncher.Test.Update;

public sealed class UpdateOrchestratorTests
(
    ITestOutputHelper output
)
{
    [Fact]
    public async Task CheckForUpdates_WithRealUrl_DetectsDeltaUpdate()
    {
        const string appId       = "XIVLauncherCN";
        const string baseVersion = "2.1.9";

        var packagesDir = Path.Combine(Path.GetTempPath(), "packages");

        // 注入本地基准 full 包, 否则 Velopack 1.0 因找不到 delta 起点而禁用增量更新
        var localPackage = new VelopackAsset
        {
            PackageId = appId,
            Version   = SemanticVersion.Parse(baseVersion),
            Type      = VelopackAssetType.Full,
            FileName  = $"{appId}-{baseVersion}-full.nupkg"
        };

        var locator = new TestVelopackLocator
        (
            appId,
            baseVersion,
            packagesDir,
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.Combine(Path.GetTempPath(), "Update.exe"),
            "win",
            localPackage: localPackage
        );
        var options = new UpdateOptions
        {
            ExplicitChannel       = "win",
            AllowVersionDowngrade = false
        };

        var downloader   = new XLHttpClientFileDownloader();
        var updateSource = new SimpleWebSource(Links.LAUNCHER_DISTRIBUTE_BASE_URL, downloader);
        var manager      = new UpdateManager(updateSource, options, locator);

        output.WriteLine($"Checking for updates from: {Links.LAUNCHER_DISTRIBUTE_BASE_URL}");
        output.WriteLine($"Current version: {locator.CurrentlyInstalledVersion}");
        output.WriteLine($"Channel: {options.ExplicitChannel}");

        var updateInfo = await manager.CheckForUpdatesAsync();

        if (updateInfo == null)
            output.WriteLine("Result: No update available - current version is up to date");
        else
        {
            output.WriteLine("Result: Update available");
            output.WriteLine($"  Target Version: {updateInfo.TargetFullRelease.Version}");
            output.WriteLine($"  Package ID: {updateInfo.TargetFullRelease.PackageId}");
            output.WriteLine($"  File Name: {updateInfo.TargetFullRelease.FileName}");
            output.WriteLine($"  Type: {updateInfo.TargetFullRelease.Type}");
            output.WriteLine($"  Size: {updateInfo.TargetFullRelease.Size:N0} bytes");

            if (updateInfo.DeltasToTarget.Length != 0)
            {
                output.WriteLine($"  Delta packages available: {updateInfo.DeltasToTarget.Length}");
                foreach (var delta in updateInfo.DeltasToTarget) output.WriteLine($"    - {delta.FileName} ({delta.Size:N0} bytes)");
            }
            else
                output.WriteLine("  No delta packages available - will use full package");
        }

        // feed 中存在比本地更新的版本, 应检测到更新且优先走增量包
        Assert.NotNull(updateInfo);
        Assert.NotNull(updateInfo.TargetFullRelease);
        Assert.NotEmpty(updateInfo.DeltasToTarget);
        Assert.All(updateInfo.DeltasToTarget, delta => Assert.Equal(VelopackAssetType.Delta, delta.Type));
    }

    [Fact]
    public async Task CheckForUpdates_WithInvalidUrl_ThrowsException()
    {
        var locator = new TestVelopackLocator("XIVLauncherCN", "2.1.4", Path.Combine(Path.GetTempPath(), "packages"));
        var options = new UpdateOptions
        {
            ExplicitChannel       = "win",
            AllowVersionDowngrade = false
        };

        var          downloader   = new XLHttpClientFileDownloader();
        const string INVALID_URL  = "https://invalid-url-that-does-not-exist.example.com";
        var          updateSource = new SimpleWebSource(INVALID_URL, downloader);
        var          manager      = new UpdateManager(updateSource, options, locator);

        output.WriteLine($"Attempting to check updates from invalid URL: {INVALID_URL}");

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () => await manager.CheckForUpdatesAsync());

        output.WriteLine("Expected exception caught:");
        output.WriteLine($"  Type: {exception.GetType().Name}");
        output.WriteLine($"  Message: {exception.Message}");
    }
}
