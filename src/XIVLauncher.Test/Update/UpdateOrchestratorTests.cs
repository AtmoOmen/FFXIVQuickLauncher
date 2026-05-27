using System.IO;
using System.Linq;
using NuGet.Versioning;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
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
    public async Task CheckForUpdates_WithRealUrl_ReturnsUpdateInfoOrNull()
    {
        var locator = new TestVelopackLocator();
        var options = new UpdateOptions
        {
            ExplicitChannel       = "win",
            AllowVersionDowngrade = false
        };

        var downloader   = new XLHttpClientFileDownloader();
        var updateSource = new SimpleWebSource(Links.LAUNCHER_DISTRIBUTE_BASE_URL, downloader);
        var manager      = new UpdateManager(updateSource, options, locator);

        output.WriteLine($"Checking for updates from: {Links.LAUNCHER_DISTRIBUTE_BASE_URL}");
        output.WriteLine($"Current version: {locator.CurrentVersion}");
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

            if (updateInfo.DeltasToTarget.Any())
            {
                output.WriteLine($"  Delta packages available: {updateInfo.DeltasToTarget.Count()}");
                foreach (var delta in updateInfo.DeltasToTarget)
                {
                    output.WriteLine($"    - {delta.FileName} ({delta.Size:N0} bytes)");
                }
            }
            else
            {
                output.WriteLine("  No delta packages available - will use full package");
            }
        }

        Assert.True(updateInfo == null || updateInfo.TargetFullRelease != null);
    }

    [Fact]
    public async Task CheckForUpdates_WithInvalidUrl_ThrowsException()
    {
        var locator = new TestVelopackLocator();
        var options = new UpdateOptions
        {
            ExplicitChannel       = "win",
            AllowVersionDowngrade = false
        };

        var downloader   = new XLHttpClientFileDownloader();
        var invalidUrl   = "https://invalid-url-that-does-not-exist.example.com";
        var updateSource = new SimpleWebSource(invalidUrl, downloader);
        var manager      = new UpdateManager(updateSource, options, locator);

        output.WriteLine($"Attempting to check updates from invalid URL: {invalidUrl}");

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () => await manager.CheckForUpdatesAsync());

        output.WriteLine("Expected exception caught:");
        output.WriteLine($"  Type: {exception.GetType().Name}");
        output.WriteLine($"  Message: {exception.Message}");
    }

    private sealed class TestVelopackLocator : IVelopackLocator
    {
        public string          AppId                     => "XIVLauncherCN";
        public string          RootAppDir                => Path.GetTempPath();
        public string          UpdateExePath             => Path.Combine(Path.GetTempPath(), "Update.exe");
        public string          CurrentBinaryDir          => Path.GetTempPath();
        public string          PackagesDir               => Path.Combine(Path.GetTempPath(), "packages");
        public string          AppContentDir             => Path.GetTempPath();
        public string          AppTempDir                => Path.GetTempPath();
        public string          ProcessExePath            => Environment.ProcessPath ?? string.Empty;
        public string          ThisExeRelativePath       => "XIVLauncherCN.exe";
        public uint            ProcessId                 => (uint)Environment.ProcessId;
        public bool            IsPortable                => false;
        public SemanticVersion CurrentVersion            => SemanticVersion.Parse("2.1.4");
        public SemanticVersion CurrentlyInstalledVersion => SemanticVersion.Parse("2.1.4");
        public string?         Channel                   => "win";
        public VelopackAsset?  CurrentAsset              => null;
        public IVelopackLogger Log                       => new NullLogger();

        public VelopackAsset GetLatestLocalFullPackage()
        {
            return new VelopackAsset
            {
                PackageId = AppId,
                Version   = CurrentVersion,
                Type      = VelopackAssetType.Full,
                FileName  = $"{AppId}-{CurrentVersion}-full.nupkg"
            };
        }

        public string GetManifestPath(VelopackAsset asset) =>
            Path.Combine(PackagesDir, $"{asset.FileName}.manifest");

        public List<VelopackAsset> GetLocalPackages() => [];

        public Guid? GetOrCreateStagedUserId() => Guid.NewGuid();

        private sealed class NullLogger : IVelopackLogger
        {
            public void Log(VelopackLogLevel level, string? message, Exception? exception = null) { }
        }
    }
}
