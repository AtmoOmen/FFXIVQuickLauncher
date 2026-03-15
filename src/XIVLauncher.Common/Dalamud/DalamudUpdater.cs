using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.Http;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Dalamud;

public class DalamudUpdater
{
    public DirectoryInfo Runtime { get; }

    public static string RuntimeVersion = string.Empty;

    public        DownloadState           State               { get; private set; } = DownloadState.Unknown;
    public        Exception?              EnsurementException { get; private set; }
    public        FileInfo?               RunnerOverride      { get; set; }
    public        DirectoryInfo?          AssetDirectory      { get; private set; }
    public        IDalamudLoadingOverlay? Overlay             { get; set; }
    public static string                  OnlineHash          { get; private set; } = string.Empty;
    public static string                  Version             { get; private set; } = string.Empty;

    public FileInfo? Runner
    {
        get => RunnerOverride ?? field;
        private set;
    }

    private readonly DirectoryInfo addonDirectory;
    private readonly DirectoryInfo assetDirectory;
    private readonly string?       githubToken;

    private readonly TimeSpan   defaultTimeout = TimeSpan.FromMinutes(1);
    private readonly HttpClient httpClient;

    private bool forceProxy;

    public DalamudUpdater
    (
        DirectoryInfo addonDirectory,
        DirectoryInfo runtimeDirectory,
        DirectoryInfo assetDirectory,
        string?       githubToken
    )
    {
        this.addonDirectory = addonDirectory;
        Runtime             = runtimeDirectory;
        this.assetDirectory = assetDirectory;
        this.githubToken    = githubToken;
        httpClient = new HttpClient
        (
            new SocketsHttpHandler
            {
                UseProxy                       = true,
                ConnectTimeout                 = TimeSpan.FromSeconds(10),
                MaxConnectionsPerServer        = 50,
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime       = TimeSpan.FromMinutes(1),
                Expect100ContinueTimeout       = TimeSpan.Zero,
                AutomaticDecompression         = DecompressionMethods.All,
                ConnectCallback                = HappyEyeballsCallback.ConnectCallback
            }
        );
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");
        if (!string.IsNullOrWhiteSpace(this.githubToken))
            httpClient.DefaultRequestHeaders.Authorization = new("Bearer", this.githubToken);
    }

    public void Run(bool overrideForceProxy = false)
    {
        Log.Information("[DUPDATE] 启动中... (是否强制使用代理: {ForceProxy})", overrideForceProxy);
        State = DownloadState.Unknown;

        forceProxy = overrideForceProxy;

        Task.Run
        (async () =>
            {
                const int MAX_TRIES = 10;

                var isUpdated = false;

                for (var tries = 0; tries < MAX_TRIES; tries++)
                {
                    try
                    {
                        await UpdateDalamud().ConfigureAwait(true);
                        isUpdated = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DUPDATE] 更新失败, 重试 {TryCnt}/{MaxTries}...", tries, MAX_TRIES);
                        EnsurementException = ex;
                        forceProxy          = false;
                    }
                }

                State = isUpdated ? DownloadState.Done : DownloadState.NoIntegrity;
            }
        );
    }

    public enum DownloadState
    {
        Unknown,
        Done,
        NoIntegrity // fail with error message
    }

    #region Steps

    private async Task UpdateDalamud()
    {
        try
        {
            Log.Information("[DUPDATE] 开始 Dalamud 更新进程");

            await InitVersionInfoAsync();
            var paths = PreparePaths();
            await UpdateDalamudCoreAsync(paths.addonPath, paths.currentVersionPath);
            await UpdateRuntimeAsync(paths.runtimePaths);
            await UpdateAssetsAsync();

            // 最终验证
            if (!CheckDalamudIntegrity(paths.currentVersionPath))
                throw new DalamudIntegrityException("完整性验证最终失败");

            Runner = new FileInfo(Path.Combine(paths.currentVersionPath.FullName, "Dalamud.Injector.exe"));
            SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Starting);
            ReportOverlayProgress(null, 0, null);

            Log.Information($"[DUPDATE] Dalamud {Version} ({OnlineHash}) 准备完毕");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 更新 Dalamud 过程中发生错误");
            throw;
        }
    }

    private async Task InitVersionInfoAsync()
    {
        Log.Verbose("[DUPDATE] 开始检查版本信息");

        if (!string.IsNullOrWhiteSpace(OnlineHash) && !string.IsNullOrWhiteSpace(Version))
        {
            Log.Verbose("[DUPDATE] 版本信息已存在: {Version} ({Hash})", Version, OnlineHash);
            return;
        }

        Log.Information("[DUPDATE] 正在从 Github 获取最新版本信息");
        await GetDalamudVersionInfoAsync();
        Log.Information("[DUPDATE] 获取到版本: {Version} ({Hash})", Version, OnlineHash);
    }

    private (DirectoryInfo addonPath, DirectoryInfo currentVersionPath, DirectoryInfo[] runtimePaths) PreparePaths()
    {
        Log.Verbose("[DUPDATE] 开始准备路径信息");

        var addonPath          = new DirectoryInfo(Path.Combine(addonDirectory.FullName, "Hooks"));
        var currentVersionPath = new DirectoryInfo(Path.Combine(addonPath.FullName,      Version));

        var runtimePaths = new DirectoryInfo[]
        {
            new(Path.Combine(Runtime.FullName, "host",   "fxr",                          RuntimeVersion)),
            new(Path.Combine(Runtime.FullName, "shared", "Microsoft.NETCore.App",        RuntimeVersion)),
            new(Path.Combine(Runtime.FullName, "shared", "Microsoft.WindowsDesktop.App", RuntimeVersion))
        };

        Log.Verbose
        (
            "[DUPDATE] 路径信息: 版本路径={Path}, 运行时路径数={Count}",
            currentVersionPath.FullName,
            runtimePaths.Length
        );

        return (addonPath, currentVersionPath, runtimePaths);
    }

    private async Task UpdateDalamudCoreAsync(DirectoryInfo addonPath, DirectoryInfo currentVersionPath)
    {
        Log.Information("[DUPDATE] 开始检查 Dalamud 本体完整性");

        if (currentVersionPath.Exists && CheckDalamudIntegrity(currentVersionPath))
        {
            Log.Information("[DUPDATE] Dalamud 本体完整性检查已通过，无需更新");
            return;
        }

        Log.Information("[DUPDATE] Dalamud 本体完整性检查未通过, 开始更新");
        SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Dalamud);

        try
        {
            Log.Information("[DUPDATE] 开始下载 Dalamud 本体");
            await DownloadDalamud(currentVersionPath).ConfigureAwait(true);

            Log.Information("[DUPDATE] 清理旧版本 Dalamud 文件");
            CleanUpOld(addonPath, Version);

            Log.Information("[DUPDATE] Dalamud 本体更新完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 下载 Dalamud 本体失败");
            throw new DalamudIntegrityException("下载 Dalamud 失败", ex);
        }
    }

    private async Task UpdateRuntimeAsync(DirectoryInfo[] runtimePaths)
    {
        Log.Information("[DUPDATE] 开始检查 .NET 运行时 {Version} 完整性", RuntimeVersion);

        if (!Runtime.Exists)
        {
            Log.Verbose("[DUPDATE] 运行时目录不存在, 进行创建");
            Directory.CreateDirectory(Runtime.FullName);
        }

        var versionFile        = new FileInfo(Path.Combine(Runtime.FullName, "version"));
        var localVersion       = GetLocalRuntimeVersion(versionFile);
        var runtimeNeedsUpdate = localVersion != RuntimeVersion;

        if (runtimePaths.All(p => p.Exists) && !runtimeNeedsUpdate)
        {
            Log.Information("[DUPDATE] .NET 运行时已是最新版本: {Version}", RuntimeVersion);
            return;
        }

        Log.Information("[DUPDATE] 需要更新 .NET 运行时: 本地={LocalVer}, 目标={RemoteVer}", localVersion, RuntimeVersion);
        SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Runtime);

        try
        {
            Log.Information("[DUPDATE] 开始下载 .NET 运行时");
            await DownloadRuntime(Runtime, RuntimeVersion).ConfigureAwait(false);

            File.WriteAllText(versionFile.FullName, RuntimeVersion);
            Log.Information("[DUPDATE] .NET 运行时更新完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] .NET 运行时更新失败");
            throw new DalamudIntegrityException("无法确保运行时完整性", ex);
        }
    }

    private async Task UpdateAssetsAsync()
    {
        Log.Information("[DUPDATE] 开始验证资源文件完整性");

        SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Assets);
        ReportOverlayProgress(null, 0, null);

        try
        {
            var assetResult = await AssetManager.EnsureAssets(this, assetDirectory).ConfigureAwait(true);
            AssetDirectory = assetResult.AssetDir;
            Log.Information("[DUPDATE] 资源文件验证完成: {Path}", AssetDirectory.FullName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 资源文件验证失败");
            throw new DalamudIntegrityException("资源文件验证失败", ex);
        }
    }

    #endregion

    #region Dalamud

    public static bool CheckDalamudIntegrity(DirectoryInfo addonPath)
    {
        var files = addonPath.GetFiles();

        try
        {
            if (!CanRead(files.First(x => x.Name == "Dalamud.Injector.exe")) || !CanRead(files.First(x => x.Name == "Dalamud.dll")) || !CanRead(files.First(x => x.Name == "ImGuiScene.dll")))
            {
                Log.Error("[DUPDATE] 无法打开核心文件");
                return false;
            }

            var hashesPath = Path.Combine(addonPath.FullName, "hashes.json");

            if (!File.Exists(hashesPath))
            {
                Log.Error("[DUPDATE] 无 hashes.json");
                return false;
            }

            if (!string.IsNullOrEmpty(OnlineHash))
            {
                var hashHash = ComputeFileHash(hashesPath);

                if (OnlineHash != hashHash)
                {
                    Log.Error($"[UPDATE] hashes.json 哈希比对不一致, 本地: {hashHash}, 远程: {OnlineHash}");
                    return false;
                }
            }

            return CheckIntegrity(addonPath, File.ReadAllText(hashesPath));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 无 Dalamud 完整性");
            return false;
        }
    }

    public async Task GetDalamudVersionInfoAsync()
    {
        try
        {
            var runtimeResponse = await httpClient.GetAsync
                                  (
                                      "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/XLCNSoilAssets/refs/heads/master/runtimeInfo"
                                  );
            runtimeResponse.EnsureSuccessStatusCode();
            RuntimeVersion = await runtimeResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            RuntimeVersion = RuntimeVersion.Trim().Trim('\n');

            Log.Information("[DUPDATE] 获取到远端 Dalamud 运行时版本: {0}", RuntimeVersion);

            var response = await httpClient.GetAsync
                           (
                               "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/ghapi-json-generator/output/v2/repos/AtmoOmen/Dalamud/releases/latest/data.json"
                           );
            response.EnsureSuccessStatusCode();

            var       json    = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(json);

            var version = jsonDoc.RootElement.GetProperty("tag_name").GetString();
            if (string.IsNullOrWhiteSpace(version))
                throw new NullReferenceException("[DUPDATE] 未能找到对应的版本信息");
            Version = version;

            var assets = jsonDoc.RootElement.GetProperty("assets");

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == "hashes.json")
                {
                    var downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    if (string.IsNullOrWhiteSpace(downloadUrl)) continue;

                    var downloadPath = PlatformHelpers.GetTempFileName();

                    using (var fileResponse = await httpClient.GetAsync($"{downloadUrl}", HttpCompletionOption.ResponseHeadersRead))
                    {
                        fileResponse.EnsureSuccessStatusCode();
                        using (var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            await fileResponse.Content.CopyToAsync(fileStream);
                    }

                    var hash = ComputeFileHash(downloadPath);
                    File.Delete(downloadPath);

                    Log.Information($"[DUPDATE] 获取到远端 Dalamud 哈希: {hash}");
                    OnlineHash = hash;
                    return;
                }
            }

            throw new NullReferenceException("[DUPDATE] 未能找到对应的 hashes.json 文件");
        }
        catch (HttpRequestException e)
        {
            throw new Exception("访问 Github API 时发生错误: " + e.Message);
        }
        catch (TaskCanceledException)
        {
            throw new Exception("下载超时");
        }
        catch (OperationCanceledException)
        {
            throw new Exception("下载取消");
        }
    }

    private async Task DownloadDalamud(DirectoryInfo addonPath)
    {
        const string REPO_API = "https://gh.atmoomen.top/https://raw.githubusercontent.com/Dalamud-DailyRoutines/ghapi-json-generator/output/v2/repos/AtmoOmen/Dalamud/releases/latest/data.json";

        if (addonPath.Exists) addonPath.Delete(true);
        addonPath.Create();

        try
        {
            var response = await httpClient.GetAsync(REPO_API);
            response.EnsureSuccessStatusCode();

            var       json    = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(json);
            var       assets  = jsonDoc.RootElement.GetProperty("assets");

            var downloadPath = PlatformHelpers.GetTempFileName();

            foreach (var asset in assets.EnumerateArray())
            {
                var fileName    = asset.GetProperty("name").GetString()!;
                var downloadUrl = asset.GetProperty("browser_download_url").GetString()!;

                if (fileName != "latest.7z") continue;

                await DownloadFile($"{downloadUrl}", downloadPath, defaultTimeout).ConfigureAwait(false);
                PlatformHelpers.Unzip7ZAsset(downloadPath, addonPath.FullName);
                File.Delete(downloadPath);
                break;
            }

            try
            {
                var devPath = new DirectoryInfo(Path.Combine(addonPath.FullName, "..", "dev"));
                PlatformHelpers.DeleteAndRecreateDirectory(devPath);
                PlatformHelpers.CopyFilesRecursively(addonPath, devPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] 复制到 dev 目录失败");
            }
        }
        catch (HttpRequestException e)
        {
            Log.Error(e, "[DUPDATE] GitHub API 请求失败");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 下载过程中发生错误");
            throw;
        }
    }

    #endregion

    #region Runtime

    private async Task DownloadRuntime(DirectoryInfo runtimePath, string version)
    {
        if (runtimePath.Exists) runtimePath.Delete(true);
        runtimePath.Create();

        try
        {
            // 微软 .NET 运行时下载链接
            var packageBaseAddress = await IsGoogleReachableAsync()
                                         ? "https://api.nuget.org/v3-flatcontainer"
                                         : "https://repo.huaweicloud.com/artifactory/api/nuget/v3/nuget-remote";

            var dotnetUrl  = $"{packageBaseAddress}/microsoft.netcore.app.runtime.win-x64/{version}/microsoft.netcore.app.runtime.win-x64.{version}.nupkg";
            var desktopUrl = $"{packageBaseAddress}/microsoft.windowsdesktop.app.runtime.win-x64/{version}/microsoft.windowsdesktop.app.runtime.win-x64.{version}.nupkg";

            var downloadPath = PlatformHelpers.GetTempFileName();

            if (File.Exists(downloadPath))
                File.Delete(downloadPath);

            // 下载 .NET 运行时
            Log.Verbose("[DUPDATE] 正在下载 .NET 运行时 v{Version}...", version);
            var dotnetVersion = version.Split('.')[0];
            await DownloadNuGet(dotnetUrl, downloadPath, defaultTimeout).ConfigureAwait(false);
            ExtractSpecificDirectory(downloadPath, Path.Combine(runtimePath.FullName, "shared", "Microsoft.NETCore.App", version), "runtimes/win-x64/native/");
            ExtractSpecificDirectory(downloadPath, Path.Combine(runtimePath.FullName, "shared", "Microsoft.NETCore.App", version), $"runtimes/win-x64/lib/net{dotnetVersion}.0/");

            // 下载 Windows Desktop 运行时
            Log.Verbose("[DUPDATE] 正在下载 .NET 桌面运行时 v{Version}...", version);
            await DownloadNuGet(desktopUrl, downloadPath, defaultTimeout).ConfigureAwait(false);
            ExtractSpecificDirectory(downloadPath, Path.Combine(runtimePath.FullName, "shared", "Microsoft.WindowsDesktop.App", version), "runtimes/win-x64/native/");
            ExtractSpecificDirectory(downloadPath, Path.Combine(runtimePath.FullName, "shared", "Microsoft.WindowsDesktop.App", version), $"runtimes/win-x64/lib/net{dotnetVersion}.0/");

            Directory.CreateDirectory(Path.Combine(runtimePath.FullName, "host", "fxr", version));
            File.Move
            (
                Path.Combine(runtimePath.FullName, "shared", "Microsoft.NETCore.App", version, "hostfxr.dll"),
                Path.Combine(runtimePath.FullName, "host",   "fxr",                   version, "hostfxr.dll")
            );

            File.Delete(downloadPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 从微软下载 .NET 运行时 v{Version} 时失败", version);
        }
    }

    private string GetLocalRuntimeVersion(FileInfo versionFile)
    {
        const string DEFAULT_VERSION = "5.0.0";

        try
        {
            if (versionFile.Exists)
                return File.ReadAllText(versionFile.FullName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[DUPDATE] 无法读取本地运行时版本, 返回默认版本 {DEFAULT_VERSION}");
        }

        return DEFAULT_VERSION;
    }

    #endregion

    #region Utility

    private static bool CheckIntegrity(DirectoryInfo directory, string hashesJson)
    {
        try
        {
            Log.Verbose("[DUPDATE] 开始检查目录 {Directory} 的完整性", directory.FullName);

            var hashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(hashesJson);
            if (hashes == null) throw new ArgumentNullException(nameof(hashes));

            foreach (var hash in hashes)
            {
                var file   = Path.Combine(directory.FullName, hash.Key.Replace("\\", "/"));
                var hashed = ComputeFileHash(file);

                if (hashed != hash.Value)
                {
                    Log.Error("[DUPDATE] 完整性检查失败: {0} ({1} - {2})", file, hash.Value, hashed);
                    return false;
                }

                Log.Verbose("[DUPDATE] 完整性检查通过: {0} ({1})", file, hashed);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 完整性检查失败");
            return false;
        }

        return true;
    }

    private static bool CanRead(FileInfo info)
    {
        try
        {
            using var stream = info.OpenRead();
            stream.ReadByte();
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static string ComputeFileHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var md5    = MD5.Create();

            var hashHash = BitConverter.ToString(md5.ComputeHash(stream)).ToUpperInvariant().Replace("-", string.Empty);
            return hashHash;
        }
        catch (Exception e)
        {
            throw new Exception("Error computing file hash: " + e.Message);
        }
    }

    private static void CleanUpOld(DirectoryInfo addonPath, string currentVer)
    {
        if (!addonPath.Exists)
            return;

        foreach (var directory in addonPath.GetDirectories())
        {
            if (directory.Name == "dev" || directory.Name == currentVer) continue;

            try
            {
                directory.Delete(true);
            }
            catch
            {
                // ignored
            }
        }
    }

    public async Task DownloadFile(string url, string path, TimeSpan timeout)
    {
        if (forceProxy && url.Contains("/File/Get/"))
            url = url.Replace("/File/Get/", "/File/GetProxy/");

        using var downloader = new HttpClientDownloadWithProgress(url, path);
        downloader.ProgressChanged += ReportOverlayProgress;

        await downloader.Download(timeout).ConfigureAwait(false);
    }

    public async Task DownloadNuGet(string url, string path, TimeSpan timeout)
    {
        using var downloader = new HttpClientDownloadWithProgress(url, path);
        downloader.ProgressChanged += ReportOverlayProgress;

        await downloader.Download(timeout, true).ConfigureAwait(false);
    }

    public static void ExtractSpecificDirectory(string zipPath, string extractPath, string directoryToExtract)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith(directoryToExtract, StringComparison.OrdinalIgnoreCase))
            {
                var destinationPath      = Path.Combine(extractPath, entry.FullName[directoryToExtract.Length..]);
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);

                if (!string.IsNullOrEmpty(entry.Name))
                    entry.ExtractToFile(destinationPath, true);
            }
        }
    }

    public static async Task<bool> IsGoogleReachableAsync()
    {
        const string GOOGLE_URL = "https://www.google.com";
        const string HUAWEI_URL = "https://www.huaweicloud.com/";

        var googleTask = GetConnectionTimeAsync(GOOGLE_URL);
        var huaweiTask = GetConnectionTimeAsync(HUAWEI_URL);

        await Task.WhenAll(googleTask, huaweiTask);

        var googleResult = await googleTask;
        var huaweiResult = await huaweiTask;

        Log.Information
        (
            "谷歌连接耗时: {GoogleTime:F2} ms, 状态: {GoogleStatus}",
            googleResult.Elapsed.TotalMilliseconds,
            googleResult.IsSuccess ? "成功" : "失败"
        );

        Log.Information
        (
            "华为云连接耗时: {HuaweiTime:F2} ms, 状态: {HuaweiStatus}",
            huaweiResult.Elapsed.TotalMilliseconds,
            huaweiResult.IsSuccess ? "成功" : "失败"
        );

        if (!googleResult.IsSuccess)
        {
            Log.Warning("无法连接到谷歌");
            return false;
        }

        if (huaweiResult.IsSuccess && huaweiResult.Elapsed < googleResult.Elapsed)
        {
            Log.Information
            (
                "华为云连接速度 ({HuaweiTime:F2} ms) 快于 Google ({GoogleTime:F2} ms)",
                huaweiResult.Elapsed.TotalMilliseconds,
                googleResult.Elapsed.TotalMilliseconds
            );
            return false;
        }

        Log.Information("Google 连接成功且速度不慢于华为云");
        return true;

        async Task<(bool IsSuccess, TimeSpan Elapsed)> GetConnectionTimeAsync(string url)
        {
            var stopwatch = new Stopwatch();

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(3);

                stopwatch.Start();
                using var response = await client.GetAsync(url);
                stopwatch.Stop();

                return (response.IsSuccessStatusCode, stopwatch.Elapsed);
            }
            catch
            {
                if (stopwatch.IsRunning)
                    stopwatch.Stop();

                return (false, stopwatch.Elapsed);
            }
        }
    }

    #endregion

    #region UI

    public void SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep progress) =>
        Overlay!.SetStep(progress);

    public void ShowOverlay() =>
        Overlay!.SetVisible();

    public void CloseOverlay() =>
        Overlay!.SetInvisible();

    private void ReportOverlayProgress(long? size, long downloaded, double? progress) =>
        Overlay!.ReportProgress(size, downloaded, progress);

    #endregion
}

public class DalamudIntegrityException
(
    string     msg,
    Exception? inner = null
) : Exception(msg, inner);
