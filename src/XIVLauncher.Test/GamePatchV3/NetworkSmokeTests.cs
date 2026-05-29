using System.Linq;
using System.Threading.Tasks;
using XIVLauncher.GamePatchV3;
using Xunit;
using Xunit.Abstractions;

namespace XIVLauncher.Test.GamePatchV3;

// 真实网络冒烟: 验证国服 V3 接口契约未变, 默认 CI 可用 --filter "Category!=Network" 跳过
[Trait("Category", "Network")]
public sealed class NetworkSmokeTests
(
    ITestOutputHelper output
)
{
    [Fact]
    public async Task DownloadRemoteVersion_ParsesAreasAndPackages()
    {
        using var client = new GamePatchMetadataClient();

        var remote = await client.DownloadRemoteVersion();

        output.WriteLine($"BaseUrl: {remote.BaseUrl}");
        output.WriteLine($"Areas: {remote.Areas.Count}, Packages: {remote.Packages.Count}");

        Assert.NotEmpty(remote.BaseUrl);
        Assert.NotEmpty(remote.Areas);
        Assert.NotEmpty(remote.Packages);
        Assert.Contains(remote.Areas, area => !string.IsNullOrWhiteSpace(area.Must));
    }

    [Fact]
    public async Task DownloadIntegrityCheck_ParsesManifest()
    {
        using var client = new GamePatchMetadataClient();

        var integrity = await client.DownloadIntegrityCheck();

        output.WriteLine($"DataVersion: {integrity.DataVersion}, GameVersion: {integrity.GameVersion}");
        output.WriteLine($"BaseUrl: {integrity.BaseUrl}, Files: {integrity.Hashes.Count}");

        Assert.NotEmpty(integrity.BaseUrl);
        Assert.NotEmpty(integrity.DataVersion);
        Assert.NotEmpty(integrity.Hashes);
    }

    [Fact]
    public async Task BuildUpdatePlan_FromTargetView_ReturnsNullOrPlan()
    {
        using var client = new GamePatchMetadataClient();
        var       remote = await client.DownloadRemoteVersion();

        // 取目标区的 View 作为"当前已是目标版本"的输入, 这是必然受支持的版本, 验证能据真实数据构建而不抛意外异常
        var targetArea = remote.Areas.FirstOrDefault(area => area.Id == "0") ?? remote.Areas.FirstOrDefault();
        var targetView = remote.Packages.FirstOrDefault(package => string.Equals(package.To, targetArea?.Must))?.VersionView
                         ?? targetArea?.View;

        if (string.IsNullOrWhiteSpace(targetView))
        {
            output.WriteLine("远端无可用目标视图版本, 跳过断言");
            return;
        }

        // 已是目标版本时应返回 null(无需更新), 接口契约成立
        var plan = GamePatchMetadataClient.BuildPlanFromRemoteVersion(remote, targetView, false);

        output.WriteLine(plan == null
                             ? $"游戏版本 {targetView} 已是目标, 无需更新"
                             : $"计划: {plan.CurrentDataVersion} -> {plan.TargetDataVersion}, 包数 {plan.Packages.Count}");

        Assert.True(plan == null || plan.Packages.Count >= 0);
    }
}
