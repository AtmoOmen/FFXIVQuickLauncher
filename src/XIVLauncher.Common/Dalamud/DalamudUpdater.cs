using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;
using XIVLauncher.Common.Runtime;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Dalamud;

public class DalamudUpdater
{
    public DirectoryInfo Runtime { get; }

    public DownloadState State
    {
        get;
        private set
        {
            field = value;
            NotifyStatusChanged();
        }
    } = DownloadState.Unknown;

    public Exception?                    EnsurementException   { get; private set; }
    public FileInfo?                     RunnerOverride        { get; set; }
    public DirectoryInfo?                AssetDirectory        { get; private set; }
    public string                        LoadingDetail         { get; private set; } = string.Empty;
    public long?                         LoadingTotal          { get; private set; }
    public long                          LoadingDownloaded     { get; private set; }
    public double?                       LoadingProgress       { get; private set; }
    public Action?                       ShowLoadingCallback   { get; set; }
    public Action?                       HideLoadingCallback   { get; set; }
    public Action<string>?               SetLoadingMessage     { get; set; }
    public Action<long?, long, double?>? ReportLoadingProgress { get; set; }
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

    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private readonly DirectoryInfo addonDirectory;
    private readonly DirectoryInfo assetDirectory;
    private readonly string?       githubToken;
    private readonly SemaphoreSlim runSemaphore = new(1, 1);
    private readonly DalamudUpdateHttpMode updateHttpMode;
    private readonly FileInfo protocolStateFile = new(Path.Combine(Paths.RoamingPath, "dalamudUpdateState.json"));

    public DalamudUpdater
    (
        DirectoryInfo addonDirectory,
        DirectoryInfo runtimeDirectory,
        DirectoryInfo assetDirectory,
        string?       githubToken,
        DalamudUpdateHttpMode updateHttpMode
    )
    {
        this.addonDirectory = addonDirectory;
        Runtime             = runtimeDirectory;
        this.assetDirectory = assetDirectory;
        this.githubToken    = githubToken;
        this.updateHttpMode = updateHttpMode;
    }

    public void Run() =>
        Run(false);

    public void Run(bool refreshVersionInfo)
    {
        Log.Information("[DUPDATE] 启动 Dalamud 更新器中...");
        EnsurementException = null;
        LoadingDetail       = string.Empty;
        LoadingTotal        = null;
        LoadingDownloaded   = 0;
        LoadingProgress     = null;
        State               = DownloadState.Unknown;

        Task.Run(async () =>
        {
            await runSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                var result = await RunWithAdaptiveStrategyAsync(refreshVersionInfo).ConfigureAwait(false);
                EnsurementException = result.LastException;
                State               = result.IsSuccess ? DownloadState.Done : DownloadState.NoIntegrity;
            }
            finally
            {
                runSemaphore.Release();
            }
        });
    }

    #region Steps

    private async Task UpdateDalamud(bool refreshVersionInfo, UpdateSessionContext session, HttpClient httpClient)
    {
        try
        {
            Log.Information("[DUPDATE] 开始 Dalamud 更新进程 attempt={Attempt} protocol={Protocol}", session.Attempt, session.Protocol);

            await InitVersionInfoAsync(refreshVersionInfo, httpClient);
            var paths                  = PreparePaths();
            var currentVersionVerified = await UpdateDalamudCoreAsync(paths.addonPath, paths.currentVersionPath, session, httpClient);
            await UpdateRuntimeAsync(httpClient);
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

    private async Task InitVersionInfoAsync(bool refreshVersionInfo, HttpClient httpClient)
    {
        Log.Verbose("[DUPDATE] 开始检查版本信息");

        if (!refreshVersionInfo && !string.IsNullOrWhiteSpace(OnlineHash) && !string.IsNullOrWhiteSpace(Version))
        {
            Log.Verbose("[DUPDATE] 版本信息已存在: {Version} ({Hash})", Version, OnlineHash);
            return;
        }

        Log.Information("[DUPDATE] 正在从 Github 获取最新版本信息");
        await GetDalamudVersionInfoAsync(httpClient);
        Log.Information("[DUPDATE] 获取到版本: {Version} ({Hash})", Version, OnlineHash);
    }

    private (DirectoryInfo addonPath, DirectoryInfo currentVersionPath) PreparePaths()
    {
        Log.Verbose("[DUPDATE] 开始准备路径信息");

        var addonPath          = new DirectoryInfo(Path.Combine(addonDirectory.FullName, "Hooks"));
        var currentVersionPath = new DirectoryInfo(Path.Combine(addonPath.FullName,      Version));

        Log.Verbose("[DUPDATE] 路径信息: 版本路径={Path}", currentVersionPath.FullName);
        return (addonPath, currentVersionPath);
    }

    private async Task<bool> UpdateDalamudCoreAsync(DirectoryInfo addonPath, DirectoryInfo currentVersionPath, UpdateSessionContext session, HttpClient metadataHttpClient)
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
            await DownloadDalamud(currentVersionPath, metadataHttpClient, session).ConfigureAwait(true);

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

    private async Task UpdateRuntimeAsync(HttpClient httpClient) =>
        await DotNetRuntimeManager.EnsureRuntimeAsync
        (
            Runtime,
            runtimeVersion,
            "win-x64",
            ".NET 运行时",
            SetLoadingDetail,
            ReportLoadingProgressCore,
            cancellationToken: default
        ).ConfigureAwait(false);

    private async Task UpdateAssetsAsync()
    {
        Log.Information("[DUPDATE] 开始验证资源文件完整性");

        SetLoadingDetail("正在更新资源文件...");
        ReportLoadingProgressCore(null, 0, null);

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

    private async Task<bool> TryUpdateDalamudIncrementally
    (
        DirectoryInfo              addonPath,
        DirectoryInfo              devPath,
        Dictionary<string, string> assets,
        string                     manifestUrl,
        HttpClient                 httpClient,
        UpdateSessionContext       session
    )
    {
        var       manifestJson = await httpClient.GetStringAsync(manifestUrl).ConfigureAwait(false);
        using var manifestDoc  = JsonDocument.Parse(manifestJson);
        var       manifest     = ReadDalamudManifest(manifestDoc.RootElement);

        if (!assets.TryGetValue(manifest.HashesAsset, out var hashesUrl))
            throw new InvalidDataException("[DUPDATE] Dalamud 文件清单缺少 hashes.json 资产");

        var plan = BuildDalamudUpdatePlan(addonPath, devPath, assets, manifest.HashesAsset, manifest.Files);

        var remoteFileCount = plan.DownloadFiles.Count;

        foreach (var file in plan.CopyFiles)
        {
            Log.Verbose("[DUPDATE] 从 dev 目录复用 Dalamud 文件: {Path}", Path.GetRelativePath(addonPath.FullName, file.TargetPath));
            Directory.CreateDirectory(Path.GetDirectoryName(file.TargetPath)!);
            File.Copy(file.SourcePath, file.TargetPath, true);
        }

        plan.DownloadFiles.Add((hashesUrl, GetReleaseFilePath(addonPath, manifest.HashesAsset), OnlineHash));
        using var downloadHttpClient = CreateDalamudDownloadHttpClient(session.Protocol);
        await DownloadVerifiedFiles(plan.DownloadFiles, downloadHttpClient, session).ConfigureAwait(false);
        CleanDalamudReleaseDirectory(addonPath, plan.KeepPaths);

        Log.Information
        (
            "[DUPDATE] Dalamud 按需更新完成: 复用 {CopyCount}, 下载 {DownloadCount}, 变更 {ChangedCount}/{TotalCount}",
            plan.CopyFiles.Count,
            remoteFileCount,
            plan.ChangedFiles,
            manifest.Files.Count
        );

        return true;
    }

    private static (string HashesAsset, List<(string Path, string Hash, string Asset)> Files) ReadDalamudManifest(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("version", out var manifestVersionProperty))
            throw new InvalidDataException("[DUPDATE] Dalamud 文件清单为空");

        var manifestVersion = manifestVersionProperty.GetInt32();

        if (manifestVersion != RELEASE_MANIFEST_VERSION)
            throw new InvalidDataException($"[DUPDATE] 不支持的 Dalamud 文件清单版本: {manifestVersion}");

        var hashAlgorithm = GetRequiredStringProperty(manifest, "hashAlgorithm", "[DUPDATE] Dalamud 文件清单缺少哈希算法");

        if (!string.Equals(hashAlgorithm, RELEASE_HASH_ALGORITHM, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"[DUPDATE] 不支持的 Dalamud 哈希算法: {hashAlgorithm}");

        if (!manifest.TryGetProperty("files", out var manifestFiles) || manifestFiles.ValueKind != JsonValueKind.Array || manifestFiles.GetArrayLength() == 0)
            throw new InvalidDataException("[DUPDATE] Dalamud 文件清单没有文件项");

        var hashesAsset = GetRequiredStringProperty(manifest, "hashesAsset", "[DUPDATE] Dalamud 文件清单缺少 hashes.json 资产");
        var files       = new List<(string Path, string Hash, string Asset)>(manifestFiles.GetArrayLength());

        foreach (var file in manifestFiles.EnumerateArray())
        {
            var filePath  = GetRequiredStringProperty(file, "path",  "[DUPDATE] Dalamud 文件清单包含无效文件项");
            var fileHash  = GetRequiredStringProperty(file, "hash",  "[DUPDATE] Dalamud 文件清单包含无效文件项");
            var fileAsset = GetRequiredStringProperty(file, "asset", "[DUPDATE] Dalamud 文件清单包含无效文件项");

            if (!file.TryGetProperty("size", out var fileSizeProperty) || !fileSizeProperty.TryGetInt64(out var fileSize) || fileSize < 0)
                throw new InvalidDataException("[DUPDATE] Dalamud 文件清单包含无效文件项");

            files.Add((filePath, fileHash, fileAsset));
        }

        return (hashesAsset, files);
    }

    private static (HashSet<string> KeepPaths, List<(string SourcePath, string TargetPath)> CopyFiles, List<(string Url, string Path, string Hash)> DownloadFiles, int ChangedFiles)
        BuildDalamudUpdatePlan
        (
            DirectoryInfo                                           addonPath,
            DirectoryInfo                                           devPath,
            Dictionary<string, string>                              assets,
            string                                                  hashesAsset,
            IReadOnlyList<(string Path, string Hash, string Asset)> manifestFiles
        )
    {
        var keepPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeReleasePath(hashesAsset)
        };

        var copyFiles     = new List<(string SourcePath, string TargetPath)>();
        var downloadFiles = new List<(string Url, string Path, string Hash)>();
        var changedFiles  = 0;

        foreach (var file in manifestFiles)
        {
            keepPaths.Add(NormalizeReleasePath(file.Path));
            var targetPath = GetReleaseFilePath(addonPath, file.Path);

            if (FileMatchesHash(targetPath, file.Hash))
            {
                Log.Verbose("[DUPDATE] Dalamud 文件已是最新: {Path}", file.Path);
                continue;
            }

            changedFiles++;
            var oldFilePath = GetReleaseFilePath(devPath, file.Path);

            if (FileMatchesHash(oldFilePath, file.Hash))
            {
                copyFiles.Add((oldFilePath, targetPath));
                continue;
            }

            if (!assets.TryGetValue(file.Asset, out var fileUrl))
                throw new InvalidDataException($"[DUPDATE] Dalamud 文件资产缺失: {file.Asset}");

            downloadFiles.Add((fileUrl, targetPath, file.Hash));
        }

        return (keepPaths, copyFiles, downloadFiles, changedFiles);
    }

    private static string GetRequiredStringProperty(JsonElement element, string propertyName, string message)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            throw new InvalidDataException(message);

        var value = property.GetString();

        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException(message);

        return value;
    }

    private static bool HasReusableDalamudSeed(DirectoryInfo directory) =>
        directory.Exists
        && directory.EnumerateFiles("*", SearchOption.AllDirectories).Any
        (file => !string.Equals(file.Name, RELEASE_HASHES_ASSET, StringComparison.OrdinalIgnoreCase) && !file.Name.EndsWith(".download", StringComparison.OrdinalIgnoreCase)
        );

    private static void CleanDalamudReleaseDirectory(DirectoryInfo addonPath, HashSet<string> keepPaths)
    {
        foreach (var file in addonPath.GetFiles("*", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeReleasePath(Path.GetRelativePath(addonPath.FullName, file.FullName));

            if (!keepPaths.Contains(relativePath))
                file.Delete();
        }

        foreach (var directory in addonPath.GetDirectories("*", SearchOption.AllDirectories).OrderByDescending(directory => directory.FullName.Length))
        {
            if (!directory.EnumerateFileSystemInfos().Any())
                directory.Delete();
        }
    }

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

    public async Task GetDalamudVersionInfoAsync(HttpClient httpClient)
    {
        try
        {
            runtimeVersion = await DotNetRuntimeManager.GetLatestVersionAsync(httpClient).ConfigureAwait(false);

            Log.Information("[DUPDATE] 获取到远端 Dalamud 运行时版本: {0}", runtimeVersion);

            var response = await httpClient.GetAsync(Links.DALAMUD_RELEASE_INFO_URL);
            await response.EnsureSuccessWithDiagnosticsAsync().ConfigureAwait(false);

            var    json    = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(json ?? string.Empty);

            var version = jsonDoc.RootElement.GetProperty("tag_name").GetString();
            if (string.IsNullOrWhiteSpace(version))
                throw new NullReferenceException("[DUPDATE] 未能找到对应的版本信息");
            Version = version;

            var assets    = jsonDoc.RootElement.GetProperty("assets");
            var assetUrls = GetReleaseAssetUrls(assets);

            if (!assetUrls.TryGetValue(RELEASE_HASHES_ASSET, out var downloadUrl))
                throw new NullReferenceException("[DUPDATE] 未能找到对应的 hashes.json 文件");

            var downloadPath = PlatformHelpers.GetTempFileName();

            try
            {
                using (var fileResponse = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    await fileResponse.EnsureSuccessWithDiagnosticsAsync().ConfigureAwait(false);
                    using var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None);
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
        catch (HttpRequestException e)
        {
            Log.Error(e, "[DUPDATE] 访问 Github API 时发生错误");
            throw;
        }
        catch (TaskCanceledException e)
        {
            Log.Error(e, "[DUPDATE] 下载超时");
            throw;
        }
        catch (OperationCanceledException e)
        {
            Log.Error(e, "[DUPDATE] 下载取消");
            throw;
        }
    }

    private async Task DownloadDalamud(DirectoryInfo addonPath, HttpClient metadataHttpClient, UpdateSessionContext session)
    {
        try
        {
            var response = await metadataHttpClient.GetAsync(Links.DALAMUD_RELEASE_INFO_URL);
            await response.EnsureSuccessWithDiagnosticsAsync().ConfigureAwait(false);

            var       json         = await response.Content.ReadAsStringAsync();
            using var jsonDoc      = JsonDocument.Parse(json);
            var       assets       = GetReleaseAssetUrls(jsonDoc.RootElement.GetProperty("assets"));
            var       devPath      = new DirectoryInfo(Path.Combine(addonPath.FullName, "..", "dev"));
            var       hasLocalSeed = HasReusableDalamudSeed(addonPath) || HasReusableDalamudSeed(devPath);

            if (!addonPath.Exists)
                addonPath.Create();

            if (hasLocalSeed && assets.TryGetValue(RELEASE_MANIFEST_ASSET, out var manifestUrl))
            {
                Log.Information("[DUPDATE] 发现 Dalamud 文件清单, 开始按需更新");

                var incrementalUpdated = await TryUpdateDalamudIncrementally(addonPath, devPath, assets, manifestUrl, metadataHttpClient, session).ConfigureAwait(false);

                if (!incrementalUpdated)
                    await DownloadDalamudPackage(addonPath, assets, session).ConfigureAwait(false);
            }
            else
            {
                Log.Information(hasLocalSeed ? "[DUPDATE] 未发现 Dalamud 文件清单, 开始下载完整包" : "[DUPDATE] 未发现可复用 Dalamud 本地基线, 开始下载完整包");
                await DownloadDalamudPackage(addonPath, assets, session).ConfigureAwait(false);
            }

            CachedFileHashes.Clear();
            RefreshDalamudDevCache(addonPath, devPath);
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

    #region Utility

    private async Task DownloadDalamudPackage(DirectoryInfo addonPath, Dictionary<string, string> assets, UpdateSessionContext session)
    {
        if (!assets.TryGetValue(RELEASE_PACKAGE_ASSET, out var downloadUrl))
            throw new InvalidDataException("[DUPDATE] 未找到 Dalamud 完整包");

        if (addonPath.Exists) addonPath.Delete(true);
        addonPath.Create();

        var downloadPath = PlatformHelpers.GetTempFileName();

        try
        {
            using var downloadHttpClient = CreateDalamudDownloadHttpClient(session.Protocol);
            await DownloadFiles([(downloadUrl, downloadPath)], downloadHttpClient, session).ConfigureAwait(false);
            PlatformHelpers.Unzip7ZAsset(downloadPath, addonPath.FullName);
        }
        finally
        {
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
        }
    }

    private async Task DownloadVerifiedFiles(IReadOnlyList<(string Url, string Path, string Hash)> files, HttpClient httpClient, UpdateSessionContext session)
    {
        if (files.Count == 0)
            return;

        var downloads = files.Select(file => (file.Url, Path: $"{file.Path}.download")).ToList();

        foreach (var download in downloads)
        {
            if (File.Exists(download.Path))
                File.Delete(download.Path);
        }

        try
        {
            await DownloadFiles(downloads, httpClient, session).ConfigureAwait(false);

            for (var i = 0; i < files.Count; i++)
            {
                var file         = files[i];
                var downloadPath = downloads[i].Path;
                var fileHash     = ComputeFileHash(downloadPath);

                if (!string.Equals(fileHash, file.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new DalamudIntegrityException($"文件哈希不一致, 本地: {fileHash}, 远端: {file.Hash}");

                var directory = Path.GetDirectoryName(file.Path);

                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                File.Move(downloadPath, file.Path, true);
            }
        }
        finally
        {
            foreach (var download in downloads)
            {
                if (File.Exists(download.Path))
                    File.Delete(download.Path);
            }
        }
    }

    private async Task DownloadFiles(IReadOnlyList<(string Url, string Path)> files, HttpClient httpClient, UpdateSessionContext session)
    {
        using var downloader = new HttpClientDownloadWithProgress(files, httpClient, NoProgressTimeout);
        downloader.ProgressChanged += (size, downloaded, progress) =>
        {
            var hadProgress = downloaded > session.LastDownloadedBytes;
            session.LastAttemptHadProgress = session.LastAttemptHadProgress || hadProgress;
            session.LastDownloadedBytes    = downloaded;
            session.LastNoProgressDuration = hadProgress ? TimeSpan.Zero : NoProgressTimeout;
            ReportLoadingProgressCore(size, downloaded, progress);
        };

        await downloader.Download().ConfigureAwait(false);
    }

    private static Dictionary<string, string> GetReleaseAssetUrls(JsonElement assets)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameProperty) || !asset.TryGetProperty("browser_download_url", out var urlProperty))
                continue;

            var name = nameProperty.GetString();
            var url  = urlProperty.GetString();

            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                result[name] = url;
        }

        return result;
    }

    private static string GetReleaseFilePath(DirectoryInfo directory, string relativePath)
    {
        var normalizedPath = NormalizeReleasePath(relativePath);

        if (string.IsNullOrWhiteSpace(normalizedPath) || Path.IsPathRooted(normalizedPath) || normalizedPath.Split('/').Any(part => part == ".."))
            throw new InvalidDataException($"非法 Dalamud 文件路径: {relativePath}");

        return Path.Combine(directory.FullName, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string NormalizeReleasePath(string path) =>
        path.Replace('\\', '/');

    private static bool FileMatchesHash(string path, string expectedHash)
    {
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(expectedHash))
            return false;

        try
        {
            return string.Equals(ComputeFileHash(path), expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 无法校验文件哈希: {Path}", path);
            return false;
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

    public async Task DownloadFile(string url, string path)
    {
        using var httpClient = CreateHttpClient(ResolveInitialProtocol());
        httpClient.Timeout = DalamudDownloadTimeout;
        var session = new UpdateSessionContext { Protocol = ResolveInitialProtocol() };
        await DownloadFiles([(url, path)], httpClient, session).ConfigureAwait(false);
    }

    #endregion

    #region UI

    private void SetLoadingDetail(string message)
    {
        LoadingDetail = message;
        SetLoadingMessage?.Invoke(message);
        NotifyStatusChanged();
    }

    public void ShowLoading() =>
        ShowLoadingCallback?.Invoke();

    public void HideLoading() =>
        HideLoadingCallback?.Invoke();

    private void ReportLoadingProgressCore(long? size, long downloaded, double? progress)
    {
        LoadingTotal      = size;
        LoadingDownloaded = downloaded;
        LoadingProgress   = progress;
        ReportLoadingProgress?.Invoke(size, downloaded, progress);
        NotifyStatusChanged();
    }

    private void NotifyStatusChanged() =>
        StatusChanged?.Invoke(this);

    #endregion

    #region Strategy

    private async Task<UpdateExecutionResult> RunWithAdaptiveStrategyAsync(bool refreshVersionInfo)
    {
        var session = new UpdateSessionContext
        {
            StartedAtUtc = DateTime.UtcNow,
            Protocol     = ResolveInitialProtocol(),
            UpdateMode   = updateHttpMode
        };

        Exception? lastException = null;
        var fallbackTriggered = false;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var previousAttemptHadProgress = session.LastAttemptHadProgress;
            session.Attempt            = attempt;
            session.Elapsed            = DateTime.UtcNow - session.StartedAtUtc;
            session.LastDownloadedBytes = 0;
            session.LastAttemptHadProgress = false;
            session.LastNoProgressDuration = TimeSpan.Zero;

            if (attempt > 1 && session.Elapsed >= SoftBudget && !previousAttemptHadProgress)
            {
                Log.Warning("[DUPDATE] 软预算已耗尽且无有效进度，终止重试。attempt={Attempt} protocol={Protocol} elapsed={Elapsed}",
                    attempt, session.Protocol, session.Elapsed);
                break;
            }

            try
            {
                using var httpClient = CreateHttpClient(session.Protocol);
                await UpdateDalamud(refreshVersionInfo, session, httpClient).ConfigureAwait(false);
                SaveProtocolState(session.Protocol);
                return new UpdateExecutionResult(true, null);
            }
            catch (Exception ex)
            {
                lastException = ex;
                var errorKind = ClassifyError(ex);
                session.Elapsed = DateTime.UtcNow - session.StartedAtUtc;

                Log.Error(ex,
                    "[DUPDATE] 更新失败 attempt={Attempt} protocol={Protocol} errorKind={ErrorKind} fallbackTriggered={FallbackTriggered} noProgressDuration={NoProgressDuration} elapsed={Elapsed}",
                    attempt,
                    session.Protocol,
                    errorKind,
                    fallbackTriggered,
                    session.LastNoProgressDuration,
                    session.Elapsed);

                if (errorKind == UpdateErrorKind.NonRetryable)
                    break;

                if (ShouldFallbackProtocol(session, errorKind))
                {
                    session.Protocol = HttpProtocol.Http11;
                    session.ProtocolSwitchCount++;
                    fallbackTriggered = true;
                    SetLoadingDetail("自动切换兼容模式重试...");
                    Log.Warning("[DUPDATE] 自动切换兼容模式重试。attempt={Attempt} protocol={Protocol}", attempt, session.Protocol);
                }

                var delay = ComputeBackoffDelay(attempt);
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }

        return new UpdateExecutionResult(false, lastException);
    }

    private HttpClient CreateHttpClient(HttpProtocol protocol)
    {
        var requestVersion = protocol == HttpProtocol.Http11 ? HttpVersion.Version11 : HttpVersion.Version20;
        var versionPolicy  = protocol == HttpProtocol.Http11 ? HttpVersionPolicy.RequestVersionOrLower : HttpVersionPolicy.RequestVersionOrHigher;

        var client = XLHttpClientFactory.Create(MetadataRequestTimeout, 50, DecompressionMethods.All, requestVersion, versionPolicy);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");

        if (!string.IsNullOrWhiteSpace(this.githubToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.githubToken);

        return client;
    }

    private HttpClient CreateDalamudDownloadHttpClient(HttpProtocol protocol)
    {
        var client = CreateHttpClient(protocol);
        client.Timeout = DalamudDownloadTimeout;
        return client;
    }

    private HttpProtocol ResolveInitialProtocol()
    {
        switch (updateHttpMode)
        {
            case DalamudUpdateHttpMode.Http11:
                return HttpProtocol.Http11;

            case DalamudUpdateHttpMode.Http2:
                return HttpProtocol.Http2;

            case DalamudUpdateHttpMode.Auto:
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }

        var remembered = TryLoadProtocolState();
        if (remembered != null && DateTime.UtcNow - remembered.TimestampUtc <= ProtocolMemoryTtl)
            return remembered.Protocol;

        return HttpProtocol.Http2;
    }

    private bool ShouldFallbackProtocol(UpdateSessionContext session, UpdateErrorKind errorKind)
    {
        if (session.UpdateMode != DalamudUpdateHttpMode.Auto)
            return false;

        if (session.Protocol != HttpProtocol.Http2)
            return false;

        if (session.ProtocolSwitchCount >= MaxProtocolSwitchesPerRun)
            return false;

        return errorKind == UpdateErrorKind.Retryable;
    }

    private static UpdateErrorKind ClassifyError(Exception ex)
    {
        var current = ex;

        while (true)
        {
            switch (current)
            {
                case DalamudIntegrityException or InvalidDataException:
                    return UpdateErrorKind.NonRetryable;
                case TimeoutException:
                case TaskCanceledException or OperationCanceledException:
                    return UpdateErrorKind.Retryable;
                case UnauthorizedAccessException:
                    return UpdateErrorKind.NonRetryable;
                case HttpRequestException httpRequestException:
                    switch (httpRequestException.StatusCode)
                    {
                        case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound:
                            return UpdateErrorKind.NonRetryable;

                        case HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout:
                            return UpdateErrorKind.Retryable;
                    }

                    if (httpRequestException.InnerException is TimeoutException)
                        return UpdateErrorKind.Retryable;

                    break;
                case IOException ioException:
                    return IsRetryableIOException(ioException)
                        ? UpdateErrorKind.Retryable
                        : UpdateErrorKind.NonRetryable;
            }

            if (current.InnerException != null)
            {
                current = current.InnerException;
                continue;
            }

            return UpdateErrorKind.Retryable;
        }
    }

    private static TimeSpan ComputeBackoffDelay(int attempt)
    {
        var exponent = Math.Min(attempt - 1, 6);
        var baseSeconds = Math.Min(BackoffBaseSeconds * Math.Pow(2, exponent), BackoffMaxSeconds);
        var jitterFactor = 1 + (Random.Shared.NextDouble() * BackoffJitterRatio);
        return TimeSpan.FromSeconds(baseSeconds * jitterFactor);
    }

    private static bool IsRetryableIOException(IOException ex)
    {
        return ex is not
            (
                DirectoryNotFoundException
                or DriveNotFoundException
                or PathTooLongException
                or FileNotFoundException
            );
    }

    private ProtocolStateDto? TryLoadProtocolState()
    {
        try
        {
            if (!protocolStateFile.Exists)
                return null;

            var json = File.ReadAllText(protocolStateFile.FullName, Encoding.UTF8);
            return System.Text.Json.JsonSerializer.Deserialize<ProtocolStateDto>(json);
        }
        catch
        {
            return null;
        }
    }

    private void SaveProtocolState(HttpProtocol protocol)
    {
        try
        {
            if (protocolStateFile.Directory is { Exists: false } stateDirectory)
                stateDirectory.Create();

            var dto = new ProtocolStateDto
            {
                Protocol     = protocol,
                TimestampUtc = DateTime.UtcNow,
                Version      = Version
            };

            var json = System.Text.Json.JsonSerializer.Serialize(dto, StateJsonOptions);
            File.WriteAllText(protocolStateFile.FullName, json, Utf8WithoutBom);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DUPDATE] 写入协议记忆失败，不影响主流程");
        }
    }

    private sealed class UpdateSessionContext
    {
        public int                   Attempt;
        public DateTime              StartedAtUtc;
        public TimeSpan              Elapsed;
        public HttpProtocol          Protocol;
        public DalamudUpdateHttpMode UpdateMode;
        public int                   ProtocolSwitchCount;
        public bool                  LastAttemptHadProgress;
        public long                  LastDownloadedBytes;
        public TimeSpan              LastNoProgressDuration;
    }

    private sealed class UpdateExecutionResult(bool isSuccess, Exception? lastException)
    {
        public bool       IsSuccess     { get; } = isSuccess;
        public Exception? LastException { get; } = lastException;
    }

    private sealed class ProtocolStateDto
    {
        public HttpProtocol Protocol { get; set; }

        public DateTime TimestampUtc { get; set; }

        public string? Version { get; set; }
    }

    private enum HttpProtocol
    {
        Http2,
        Http11
    }

    private enum UpdateErrorKind
    {
        Retryable,
        NonRetryable
    }

    #endregion

    public enum DownloadState
    {
        Unknown,
        Done,
        NoIntegrity // fail with error message
    }

    #region Constants

    private const int RELEASE_MANIFEST_VERSION = 1;

    private const string RELEASE_HASH_ALGORITHM = "MD5";

    private const string RELEASE_MANIFEST_ASSET = "manifest.json";

    private const string RELEASE_PACKAGE_ASSET = "latest.7z";

    private const string RELEASE_HASHES_ASSET = "hashes.json";

    private const int MaxAttempts = 8;

    private const int MaxProtocolSwitchesPerRun = 1;

    private const double BackoffBaseSeconds = 1;

    private const double BackoffMaxSeconds = 15;

    private const double BackoffJitterRatio = 0.3;

    private static readonly TimeSpan SoftBudget = TimeSpan.FromMinutes(8);

    private static readonly TimeSpan NoProgressTimeout = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan MetadataRequestTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan ProtocolMemoryTtl = TimeSpan.FromHours(24);

    private static readonly TimeSpan DalamudDownloadTimeout = TimeSpan.FromMinutes(10);

    #endregion
}
