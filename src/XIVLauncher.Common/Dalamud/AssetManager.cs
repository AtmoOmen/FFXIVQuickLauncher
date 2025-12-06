using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Dalamud;

public class AssetManager
{
    private const string ASSET_STORE_URL = "https://gh.atmoomen.top/https://raw.githubusercontent.com/Dalamud-DailyRoutines/DalamudAssets/master/assetCN.json";

    private static readonly string[] FontUrls =
    [
        "https://mirrors.aliyun.com/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "http://mirrors.pku.edu.cn/ctan/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "https://mirror.bjtu.edu.cn/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "https://mirrors.bfsu.edu.cn/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "https://mirrors.jlu.edu.cn/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "https://mirrors.sustech.edu.cn/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "https://mirrors.nju.edu.cn/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "https://mirror.nyist.edu.cn/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "https://mirrors.tuna.tsinghua.edu.cn/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "https://mirrors.cloud.tencent.com/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "https://mirrors.ustc.edu.cn/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "https://mirrors.huaweicloud.com/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "https://mirror.lzu.edu.cn/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "https://mirrors.zju.edu.cn/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf",
        "https://mirror.iscas.ac.cn/CTAN/fonts/notocjksc/NotoSansCJKsc-Medium.otf"
    ];

    public static async Task<(DirectoryInfo AssetDir, int Version)> EnsureAssets(DalamudUpdater updater, DirectoryInfo baseDir)
    {
        using var metaClient = new HttpClient(new HttpClientHandler
        {
            // Don't Remove!!!
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
        metaClient.Timeout = TimeSpan.FromMinutes(4);

        metaClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true
        };
        
        metaClient.DefaultRequestHeaders.Add("User-Agent",      PlatformHelpers.GetVersion());
        metaClient.DefaultRequestHeaders.Add("accept-encoding", "gzip, deflate");

        using var sha1 = SHA1.Create();

        Log.Verbose("[DASSET] 开始检查 Dalamud 资源文件更新");

        var (isRefreshNeeded, info) = await CheckAssetRefreshNeeded(metaClient, baseDir);
        
        var currentDir = new DirectoryInfo(Path.Combine(baseDir.FullName, info.Version.ToString()));
        var devDir     = new DirectoryInfo(Path.Combine(baseDir.FullName, "dev"));

        var assetFileDownloadList = new List<AssetInfo.Asset>();

        foreach (var entry in info.Assets)
        {
            var filePath = Path.Combine(currentDir.FullName, entry.FileName);

            if (!File.Exists(filePath))
            {
                Log.Error("[DASSET] 未在本地发现对应的资源文件: {0}", entry.FileName);
                assetFileDownloadList.Add(entry);

                continue;
            }

            if (string.IsNullOrEmpty(entry.Hash))
                continue;

            try
            {
                using var file       = File.OpenRead(filePath);
                var       fileHash   = sha1.ComputeHash(file);
                var       stringHash = BitConverter.ToString(fileHash).Replace("-", "");

                if (stringHash != entry.Hash)
                {
                    Log.Error("[DASSET] 资源文件 {0} 不一致, 需要刷新\n本地: {1}, 远端: {2}", entry.FileName, stringHash, entry.Hash);
                    assetFileDownloadList.Add(entry);
                    //break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DASSET] 无法读取资源文件信息");
                assetFileDownloadList.Add(entry);
            }
        }

        foreach (var entry in assetFileDownloadList)
        {
            var oldFilePath = Path.Combine(devDir.FullName,     entry.FileName);
            var newFilePath = Path.Combine(currentDir.FullName, entry.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(newFilePath)!);

            try
            {
                if (File.Exists(oldFilePath))
                {
                    using var file       = File.OpenRead(oldFilePath);
                    var       fileHash   = sha1.ComputeHash(file);
                    var       stringHash = BitConverter.ToString(fileHash).Replace("-", "");

                    if (stringHash == entry.Hash)
                    {
                        Log.Verbose("[DASSET] 正在从原文件中获取资源: {0}", entry.FileName);
                        File.Copy(oldFilePath, newFilePath, true);
                        isRefreshNeeded = true;
                        continue;
                    }
                }
            }
            catch (Exception ex) { Log.Error(ex, "[DASSET] 无法从原资源文件中复制资源至新版本资源文件中: {0}", entry.FileName); }

            var fontUrls = FontUrls.ToList();
            
            var maxRetryNumber = 5;
            while (maxRetryNumber > 0)
            {
                try
                {
                    Log.Information("[DASSET] 正在下载 {0} 至 {1}...", entry.Url, entry.FileName);
                    await updater.DownloadFile(entry.Url, newFilePath, TimeSpan.FromMinutes(4));
                    isRefreshNeeded = true;
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[DASSET] 无法下载旧资源文件: {0}", entry.FileName);

                    if (entry.FileName == "UIRes/NotoSansCJKsc-Medium.otf")
                    {
                        maxRetryNumber = fontUrls.Count;
                        entry.Url      = fontUrls.First();
                        fontUrls.RemoveAt(0);
                    }

                    maxRetryNumber--;
                }
            }
        }

        if (isRefreshNeeded)
        {
            try
            {
                PlatformHelpers.DeleteAndRecreateDirectory(devDir);
                PlatformHelpers.CopyFilesRecursively(currentDir, devDir);
            }
            catch (Exception ex) { Log.Error(ex, "[DASSET] 无法将资源文件复制到 dev 文件夹中"); }

            SetLocalAssetVer(baseDir, info.Version);
        }

        Log.Verbose("[DASSET] 已完成 {0} 处的资源检查", currentDir.FullName);

        try { CleanUpOld(baseDir, devDir, currentDir); }
        catch (Exception ex) { Log.Error(ex, "[DASSET] 无法清理原资源文件"); }

        return (currentDir, info.Version);
    }

    private static string GetAssetVerPath(DirectoryInfo baseDir)
    {
        return Path.Combine(baseDir.FullName, "asset.ver");
    }

    /// <summary>
    ///     Check if an asset update is needed. When this fails, just return false - the route to github
    ///     might be bad, don't wanna just bail out in that case
    /// </summary>
    /// <param name="baseDir">Base directory for assets</param>
    /// <returns>Update state</returns>
    private static async Task<(bool isRefreshNeeded, AssetInfo info)> CheckAssetRefreshNeeded(HttpClient client, DirectoryInfo baseDir)
    {
        var localVerFile = GetAssetVerPath(baseDir);
        var localVer     = 0;

        try
        {
            if (File.Exists(localVerFile))
                localVer = int.Parse(File.ReadAllText(localVerFile));
        }
        catch (Exception ex)
        {
            // This means it'll stay on 0, which will redownload all assets - good by me
            Log.Error(ex, "[DASSET] 无法读取 asset.ver");
        }

        var remoteVer = JsonSerializer.Deserialize<AssetInfo>(await client.GetStringAsync(ASSET_STORE_URL), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Log.Verbose("[DASSET] 版本检查 - 本地:{0} 远端:{1}", localVer, remoteVer.Version);

        var needsUpdate = remoteVer.Version > localVer;

        return (needsUpdate, remoteVer);
    }

    private static void SetLocalAssetVer(DirectoryInfo baseDir, int version)
    {
        try
        {
            var localVerFile = GetAssetVerPath(baseDir);
            File.WriteAllText(localVerFile, version.ToString());
        }
        catch (Exception e) { Log.Error(e, "[DASSET] 无法写入本地资源版本信息"); }
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
        public IReadOnlyList<Asset> Assets { get; set; }

        [JsonPropertyName("packageUrl")]
        public string PackageUrl { get; set; }

        public class Asset
        {
            [JsonPropertyName("url")]
            public string Url { get; set; }

            [JsonPropertyName("fileName")]
            public string FileName { get; set; }

            [JsonPropertyName("hash")]
            public string Hash { get; set; }
        }
    }
}
