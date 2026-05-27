using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;
using XIVLauncher.Common.Runtime;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Dalamud;

public class DalamudUpdater
{
    public DirectoryInfo Runtime { get; }

    private DateTime? lastCompletionUtc;

    public DownloadState State
    {
        get;
        private set
        {
            field = value;

            if (value == DownloadState.Done)
                lastCompletionUtc = DateTime.UtcNow;

            NotifyStatusChanged();
        }
    } = DownloadState.Unknown;

    public Exception?                    EnsurementException { get; private set; }
    public FileInfo?                     RunnerOverride      { get; set; }
    public DirectoryInfo?                AssetDirectory      { get; private set; }
    public string                        LoadingDetail       { get; private set; } = string.Empty;
    public long?                         LoadingTotal        { get; private set; }
    public long                          LoadingDownloaded   { get; private set; }
    public double?                       LoadingProgress     { get; private set; }
    public IDalamudProgressSink?         ProgressSink        { get; set; }
    public event Action<DalamudUpdater>? StatusChanged;
    public static string                 OnlineHash { get; private set; } = string.Empty;
    public static string                 Version    { get; private set; } = string.Empty;

    public FileInfo? Runner
    {
        get => RunnerOverride ?? field;
        private set;
    }

    private static string runtimeVersion = string.Empty;

    private static readonly ConcurrentDictionary<string, (long Length, long LastWriteTimeUtcTicks, string Hash)> CachedFileHashes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly DirectoryInfo addonDirectory;
    private readonly DirectoryInfo assetDirectory;

    private readonly HttpClient httpClient;

    public DalamudUpdater
    (
        DirectoryInfo addonDirectory,
        DirectoryInfo runtimeDirectory,
        DirectoryInfo assetDirectory
    )
    {
        this.addonDirectory = addonDirectory;
        Runtime             = runtimeDirectory;
        this.assetDirectory = assetDirectory;
        httpClient          = XLHttpClientFactory.Create(TimeSpan.FromSeconds(10), 50, DecompressionMethods.All);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");
    }

    public void Run() =>
        Run(false);

    public void Run(bool refreshVersionInfo)
    {
        if (State == DownloadState.Done && !refreshVersionInfo)
        {
            if (lastCompletionUtc is { } lastCompletion && DateTime.UtcNow - lastCompletion < TimeSpan.FromHours(24))
            {
                Log.Information("[DUPDATE] Dalamud 更新已完成，跳过重复检查");
                return;
            }

            Log.Information("[DUPDATE] Dalamud 上次更新已超过 24 小时，将重新检查");
        }

        Log.Information("[DUPDATE] 启动 Dalamud 更新器中...");
        EnsurementException = null;
        LoadingDetail       = string.Empty;
        LoadingTotal        = null;
        LoadingDownloaded   = 0;
        LoadingProgress     = null;
        State               = DownloadState.Unknown;

        Task.Run
        (async () =>
            {
                const int MAX_TRIES = 10;

                var isUpdated = false;

                for (var tries = 0; tries < MAX_TRIES; tries++)
                {
                    try
                    {
                        await UpdateDalamud(refreshVersionInfo).ConfigureAwait(true);
                        isUpdated = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DUPDATE] 更新失败, 重试 {TryCnt}/{MaxTries}...", tries, MAX_TRIES);
                        EnsurementException = ex;
                    }
                }

                State = isUpdated ? DownloadState.Done : DownloadState.NoIntegrity;
            }
        );
    }

    #region Steps

    private async Task UpdateDalamud(bool refreshVersionInfo)
    {
        try
        {
            Log.Information("[DUPDATE] 开始 Dalamud 更新进程");

            await InitVersionInfoAsync(refreshVersionInfo);
            var paths                  = PreparePaths();
            var currentVersionVerified = await UpdateDalamudCoreAsync(paths.addonPath, paths.currentVersionPath);
            await UpdateRuntimeAsync();
            await UpdateAssetsAsync();

            if (!currentVersionVerified && !CheckDalamudIntegrity(paths.currentVersionPath))
                throw new DalamudIntegrityException("完整性验证最终失败");

            Runner = new FileInfo(Path.Combine(paths.currentVersionPath.FullName, "Dalamud.Injector.exe"));
            SetLoadingDetail("正在启动...");
            ReportLoadingProgressCore(null, 0, null);

            Log.Information("[DUPDATE] Dalamud {Version} ({OnlineHash}) 准备完毕", Version, OnlineHash);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 更新 Dalamud 过程中发生错误");
            throw;
        }
    }

    private async Task InitVersionInfoAsync(bool refreshVersionInfo)
    {
        Log.Verbose("[DUPDATE] 开始检查版本信息");

        if (!refreshVersionInfo && !string.IsNullOrWhiteSpace(OnlineHash) && !string.IsNullOrWhiteSpace(Version))
        {
            Log.Verbose("[DUPDATE] 版本信息已存在: {Version} ({Hash})", Version, OnlineHash);
            return;
        }

        Log.Information("[DUPDATE] 正在从 Github 获取最新版本信息");
        await GetDalamudVersionInfoAsync();
        Log.Information("[DUPDATE] 获取到版本: {Version} ({Hash})", Version, OnlineHash);
    }

    private (DirectoryInfo addonPath, DirectoryInfo currentVersionPath) PreparePaths()
    {
        Log.Verbose("[DUPDATE] 开始准备路径信息");

        var addonPath          = new DirectoryInfo(Path.Combine(addonDirectory.FullName, "Hooks"));
        var currentVersionPath = new DirectoryInfo(Path.Combine(addonPath.FullName,      Version));

        Log.Verbose
        (
            "[DUPDATE] 路径信息: 版本路径={Path}",
            currentVersionPath.FullName
        );

        return (addonPath, currentVersionPath);
    }

    private async Task<bool> UpdateDalamudCoreAsync(DirectoryInfo addonPath, DirectoryInfo currentVersionPath)
    {
        Log.Information("[DUPDATE] 开始检查 Dalamud 本体完整性");

        if (currentVersionPath.Exists && CheckDalamudIntegrity(currentVersionPath))
        {
            Log.Information("[DUPDATE] Dalamud 本体完整性检查已通过，无需更新");
            return true;
        }

        Log.Information("[DUPDATE] Dalamud 本体完整性检查未通过, 开始更新");
        SetLoadingDetail("正在更新核心...");

        try
        {
            Log.Information("[DUPDATE] 开始下载 Dalamud 本体");
            await DownloadDalamud(currentVersionPath).ConfigureAwait(true);

            Log.Information("[DUPDATE] 清理旧版本 Dalamud 文件");
            CleanUpOld(addonPath, Version);

            Log.Information("[DUPDATE] Dalamud 本体更新完成");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 下载 Dalamud 本体失败");
            throw new DalamudIntegrityException("下载 Dalamud 失败", ex);
        }
    }

    private async Task UpdateRuntimeAsync() =>
        await DotNetRuntimeManager.EnsureRuntimeAsync
        (
            Runtime,
            runtimeVersion,
            "win-x64",
            ".NET 运行时",
            SetLoadingDetail,
            ReportLoadingProgressCore
        ).ConfigureAwait(false);

    private async Task UpdateAssetsAsync()
    {
        Log.Information("[DUPDATE] 开始验证资源文件完整性");

        SetLoadingDetail("正在更新资源文件...");
        ReportLoadingProgressCore(null, 0, null);

        try
        {
            var assetResult = await DalamudAssetManager.EnsureAssets(this, assetDirectory).ConfigureAwait(true);
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

    private static void RefreshDalamudDevCache(DirectoryInfo addonPath, DirectoryInfo devPath)
    {
        try
        {
            PlatformHelpers.DeleteAndRecreateDirectory(devPath);
            PlatformHelpers.CopyFilesRecursively(addonPath, devPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 复制到 dev 目录失败");
        }
    }

    public static bool CheckDalamudIntegrity(DirectoryInfo addonPath)
    {
        try
        {
            var injector   = new FileInfo(Path.Combine(addonPath.FullName, "Dalamud.Injector.exe"));
            var dalamud    = new FileInfo(Path.Combine(addonPath.FullName, "Dalamud.dll"));
            var imGuiScene = new FileInfo(Path.Combine(addonPath.FullName, "ImGuiScene.dll"));

            if (!injector.Exists || !dalamud.Exists || !imGuiScene.Exists)
            {
                Log.Error("[DUPDATE] 缺少核心文件");
                return false;
            }

            if (!CanRead(injector) || !CanRead(dalamud) || !CanRead(imGuiScene))
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
        runtimeVersion = await DotNetRuntimeManager.GetLatestVersionAsync(httpClient).ConfigureAwait(false);

        Log.Information("[DUPDATE] 获取到远端 Dalamud 运行时版本: {0}", runtimeVersion);

        var releaseText = await httpClient.GetStringAsync($"{Links.DALAMUD_DISTRIBUTE_BASE_URL}/RELEASE").ConfigureAwait(false);
        var version     = releaseText.Trim();

        if (string.IsNullOrWhiteSpace(version))
            throw new NullReferenceException("[DUPDATE] 未能从 R2 获取版本信息");
        Version = version;
        Log.Information("[DUPDATE] 获取到 R2 版本: {Version}", Version);

        var hashesUrl    = $"{Links.DALAMUD_DISTRIBUTE_BASE_URL}/{Version}/hashes.json";
        var downloadPath = PlatformHelpers.GetTempFileName();

        try
        {
            using (var fileResponse = await httpClient.GetAsync(hashesUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                await fileResponse.EnsureSuccessWithDiagnosticsAsync().ConfigureAwait(false);
                using (var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    await fileResponse.Content.CopyToAsync(fileStream);
            }

            var hash = ComputeFileHash(downloadPath);

            Log.Information($"[DUPDATE] 获取到远端 Dalamud 哈希: {hash}");
            OnlineHash = hash;
        }
        finally
        {
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
        }
    }

    private async Task DownloadDalamud(DirectoryInfo addonPath)
    {
        try
        {
            var devPath = new DirectoryInfo(Path.Combine(addonPath.FullName, "..", "dev"));

            if (!addonPath.Exists)
                addonPath.Create();

            var downloadUrl = $"{Links.DALAMUD_DISTRIBUTE_BASE_URL}/{Version}/latest.7z";
            Log.Information("[DUPDATE] 从 R2 下载完整包: {Url}", downloadUrl);
            await DownloadDalamudPackage(addonPath, downloadUrl).ConfigureAwait(false);

            CachedFileHashes.Clear();
            RefreshDalamudDevCache(addonPath, devPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 下载过程中发生错误");
            throw;
        }
    }

    #endregion

    #region Utility

    private async Task DownloadDalamudPackage(DirectoryInfo addonPath, string downloadUrl)
    {
        if (addonPath.Exists) addonPath.Delete(true);
        addonPath.Create();

        var downloadPath = PlatformHelpers.GetTempFileName();

        try
        {
            await DownloadFiles([(downloadUrl, downloadPath)]).ConfigureAwait(false);
            PlatformHelpers.Unzip7ZAsset(downloadPath, addonPath.FullName);
        }
        finally
        {
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
        }
    }

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
            var fileInfo = new FileInfo(path);
            fileInfo.Refresh();

            var fileLength            = fileInfo.Length;
            var lastWriteTimeUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;

            if (CachedFileHashes.TryGetValue(path, out var cachedFileHash) && cachedFileHash.Length == fileLength && cachedFileHash.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks)
                return cachedFileHash.Hash;

            using var stream = fileInfo.Open
            (
                new FileStreamOptions
                {
                    Access     = FileAccess.Read,
                    BufferSize = 128 * 1024,
                    Mode       = FileMode.Open,
                    Options    = FileOptions.SequentialScan,
                    Share      = FileShare.ReadWrite | FileShare.Delete
                }
            );
            using var md5 = MD5.Create();

            var hash = Convert.ToHexString(md5.ComputeHash(stream));
            CachedFileHashes[path] = (fileLength, lastWriteTimeUtcTicks, hash);
            return hash;
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

    public async Task DownloadFile(string url, string path) =>
        await DownloadFiles([(url, path)]).ConfigureAwait(false);

    private async Task DownloadFiles(IReadOnlyList<(string Url, string Path)> files)
    {
        using var downloader = new HttpClientDownloadWithProgress(files);
        downloader.ProgressChanged += ReportLoadingProgressCore;

        await downloader.Download().ConfigureAwait(false);
    }

    #endregion

    #region UI

    private void SetLoadingDetail(string message)
    {
        LoadingDetail = message;
        ProgressSink?.SetLoadingMessage(message);
        NotifyStatusChanged();
    }

    public void ShowLoading() =>
        ProgressSink?.ShowLoading();

    public void HideLoading() =>
        ProgressSink?.HideLoading();

    private void ReportLoadingProgressCore(long? size, long downloaded, double? progress)
    {
        LoadingTotal      = size;
        LoadingDownloaded = downloaded;
        LoadingProgress   = progress;
        ProgressSink?.ReportLoadingProgress(size, downloaded, progress);
        NotifyStatusChanged();
    }

    private void NotifyStatusChanged() =>
        StatusChanged?.Invoke(this);

    #endregion

    public enum DownloadState
    {
        Unknown,
        Done,
        NoIntegrity // fail with error message
    }
}
