using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Dalamud;

internal class DalamudAssetManager
{
    public static async Task<(DirectoryInfo AssetDir, int Version)> EnsureAssets(DalamudUpdater updater, DirectoryInfo baseDir)
    {
        using var client = XLHttpClientFactory.Create(TimeSpan.FromSeconds(10), 50, System.Net.DecompressionMethods.None);
        client.Timeout = TimeSpan.FromMinutes(4);

        using var sha1 = SHA1.Create();

        Log.Verbose("[DASSET] 开始检查 Dalamud 资源文件更新");

        // 1. 从 R2 获取远端版本号
        var releaseUrl  = $"{Links.DALAMUD_ASSET_DISTRIBUTE_URL}/RELEASE";
        var versionText = await client.GetStringAsync(releaseUrl).ConfigureAwait(false);
        var version     = int.Parse(versionText.Trim());

        Log.Information("[DASSET] 远端资源版本: {Version}", version);

        // 2. 下载清单
        var manifestUrl = $"{Links.DALAMUD_ASSET_DISTRIBUTE_URL}/{version}/assetCN.json";
        var manifest    = JsonSerializer.Deserialize<AssetInfo>(await client.GetStringAsync(manifestUrl), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        // 3. 准备目录
        var currentDir = new DirectoryInfo(Path.Combine(baseDir.FullName, version.ToString()));
        var devDir     = new DirectoryInfo(Path.Combine(baseDir.FullName, "dev"));

        if (!currentDir.Exists)
            currentDir.Create();

        var manifestFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var needsRefresh      = false;

        // 4. 逐文件对比，按需下载
        foreach (var entry in manifest.Assets)
        {
            manifestFileNames.Add(entry.FileName);
            var filePath = Path.Combine(currentDir.FullName, entry.FileName);

            // 文件存在且哈希一致 → 跳过
            if (File.Exists(filePath) && !string.IsNullOrEmpty(entry.Hash))
            {
                try
                {
                    using var file       = File.OpenRead(filePath);
                    var       fileHash   = Convert.ToHexString(sha1.ComputeHash(file));
                    if (string.Equals(fileHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[DASSET] 无法读取资源文件: {FileName}", entry.FileName);
                }
            }

            // 尝试从 dev 缓存复用
            var devPath = Path.Combine(devDir.FullName, entry.FileName);
            if (File.Exists(devPath) && !string.IsNullOrEmpty(entry.Hash))
            {
                try
                {
                    using var devFile  = File.OpenRead(devPath);
                    var       devHash  = Convert.ToHexString(sha1.ComputeHash(devFile));
                    if (string.Equals(devHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Verbose("[DASSET] 从 dev 缓存复用: {FileName}", entry.FileName);
                        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                        File.Copy(devPath, filePath, true);
                        needsRefresh = true;
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[DASSET] 无法从 dev 缓存复用: {FileName}", entry.FileName);
                }
            }

            // 从 R2 下载
            var downloadUrl = $"{Links.DALAMUD_ASSET_DISTRIBUTE_URL}/{version}/files/{entry.FileName}";
            Log.Information("[DASSET] 下载资源文件: {Url}", downloadUrl);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            try
            {
                await updater.DownloadFile(downloadUrl, filePath).ConfigureAwait(false);
                needsRefresh = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DASSET] 下载资源文件失败: {FileName}", entry.FileName);
            }
        }

        // 5. 删除本地多余文件（不在清单中）
        if (currentDir.Exists)
        {
            foreach (var file in currentDir.GetFiles("*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(currentDir.FullName, file.FullName).Replace('\\', '/');
                if (!manifestFileNames.Contains(relativePath))
                {
                    Log.Information("[DASSET] 删除多余文件: {Path}", relativePath);
                    file.Delete();
                }
            }

            // 清理空目录
            foreach (var dir in currentDir.GetDirectories("*", SearchOption.AllDirectories).OrderByDescending(d => d.FullName.Length))
            {
                if (!dir.EnumerateFileSystemInfos().Any())
                    dir.Delete();
            }
        }

        // 6. 刷新 dev 缓存
        if (needsRefresh)
        {
            try
            {
                PlatformHelpers.DeleteAndRecreateDirectory(devDir);
                PlatformHelpers.CopyFilesRecursively(currentDir, devDir);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DASSET] 无法将资源文件复制到 dev 文件夹中");
            }

            SetLocalAssetVer(baseDir, version);
        }

        Log.Verbose("[DASSET] 已完成 {Path} 处的资源检查", currentDir.FullName);

        try
        {
            CleanUpOld(baseDir, devDir, currentDir);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DASSET] 无法清理原资源文件");
        }

        return (currentDir, version);
    }

    private static string GetAssetVerPath(DirectoryInfo baseDir) =>
        Path.Combine(baseDir.FullName, "asset.ver");

    private static void SetLocalAssetVer(DirectoryInfo baseDir, int version)
    {
        try
        {
            var localVerFile = GetAssetVerPath(baseDir);
            File.WriteAllText(localVerFile, version.ToString());
        }
        catch (Exception e)
        {
            Log.Error(e, "[DASSET] 无法写入本地资源版本信息");
        }
    }

    private static void CleanUpOld(DirectoryInfo baseDir, DirectoryInfo devDir, DirectoryInfo currentDir)
    {
        if (GameHelpers.CheckIsGameOpen())
            return;

        if (!baseDir.Exists)
            return;

        foreach (var toDelete in baseDir.GetDirectories())
        {
            if (toDelete.Name != devDir.Name && toDelete.Name != currentDir.Name)
            {
                toDelete.Delete(true);
                Log.Verbose("[DASSET] 已清理旧有资源文件: {Path}", toDelete.FullName);
            }
        }

        Log.Verbose("[DASSET] 清理完成");
    }

    internal class AssetInfo
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("assets")]
        public IReadOnlyList<Asset> Assets { get; set; } = null!;

        public class Asset
        {
            [JsonPropertyName("fileName")]
            public string FileName { get; set; } = null!;

            [JsonPropertyName("hash")]
            public string Hash { get; set; } = null!;
        }
    }
}
