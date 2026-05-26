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

        Log.Verbose("[DASSET] 开始检查 Dalamud 资源文件更新");

        // 1. 从 R2 获取远端版本号
        var releaseUrl  = $"{Links.DALAMUD_ASSET_DISTRIBUTE_URL}/RELEASE";
        var versionText = await client.GetStringAsync(releaseUrl).ConfigureAwait(false);
        var version     = int.Parse(versionText.Trim());

        Log.Information("[DASSET] 远端资源版本: {Version}", version);

        // 2. 读取本地版本号
        var localVer = ReadLocalAssetVer(baseDir);

        var currentDir = new DirectoryInfo(Path.Combine(baseDir.FullName, version.ToString()));
        var devDir     = new DirectoryInfo(Path.Combine(baseDir.FullName, "dev"));

        // 3. 版本不同 → 全量 latest.7z 覆盖
        if (localVer != version)
        {
            Log.Information("[DASSET] 版本不一致 (本地:{LocalVer} 远端:{Version})，下载全量包", localVer, version);
            await FullRefresh(updater, currentDir, version).ConfigureAwait(false);

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
            CleanUpOld(baseDir, devDir, currentDir);

            Log.Verbose("[DASSET] 全量更新完成: {Path}", currentDir.FullName);
            return (currentDir, version);
        }

        // 4. 版本相同 → 逐文件对比
        Log.Information("[DASSET] 版本一致 ({Version})，逐文件对比", version);

        var manifestUrl = $"{Links.DALAMUD_ASSET_DISTRIBUTE_URL}/{version}/assetCN.json";
        var manifest    = JsonSerializer.Deserialize<AssetInfo>(await client.GetStringAsync(manifestUrl), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        if (!currentDir.Exists)
            currentDir.Create();

        using var sha1 = SHA1.Create();

        var manifestFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var needsRefresh      = false;

        foreach (var entry in manifest.Assets)
        {
            manifestFileNames.Add(entry.FileName);
            var filePath = Path.Combine(currentDir.FullName, entry.FileName);

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
                    using var devFile = File.OpenRead(devPath);
                    var       devHash = Convert.ToHexString(sha1.ComputeHash(devFile));
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

        // 删除本地多余文件
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

            foreach (var dir in currentDir.GetDirectories("*", SearchOption.AllDirectories).OrderByDescending(d => d.FullName.Length))
            {
                if (!dir.EnumerateFileSystemInfos().Any())
                    dir.Delete();
            }
        }

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

        Log.Verbose("[DASSET] 逐文件对比完成: {Path}", currentDir.FullName);

        CleanUpOld(baseDir, devDir, currentDir);

        return (currentDir, version);
    }

    private static async Task FullRefresh(DalamudUpdater updater, DirectoryInfo currentDir, int version)
    {
        var downloadUrl = $"{Links.DALAMUD_ASSET_DISTRIBUTE_URL}/{version}/latest.7z";
        var tempPath    = PlatformHelpers.GetTempFileName();

        try
        {
            Log.Information("[DASSET] 下载全量包: {Url}", downloadUrl);
            await updater.DownloadFile(downloadUrl, tempPath).ConfigureAwait(false);

            if (currentDir.Exists)
                currentDir.Delete(true);
            currentDir.Create();

            PlatformHelpers.Unzip7ZAsset(tempPath, currentDir.FullName);
            Log.Information("[DASSET] 全量包解压完成");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static int ReadLocalAssetVer(DirectoryInfo baseDir)
    {
        try
        {
            var localVerFile = GetAssetVerPath(baseDir);
            if (File.Exists(localVerFile))
                return int.Parse(File.ReadAllText(localVerFile));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DASSET] 无法读取 asset.ver");
        }

        return 0;
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
