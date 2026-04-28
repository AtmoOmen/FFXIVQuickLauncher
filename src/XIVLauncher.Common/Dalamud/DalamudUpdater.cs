using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
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

    public        DownloadState                 State                 { get; private set; } = DownloadState.Unknown;
    public        Exception?                    EnsurementException   { get; private set; }
    public        FileInfo?                     RunnerOverride        { get; set; }
    public        DirectoryInfo?                AssetDirectory        { get; private set; }
    public        Action?                       ShowLoadingCallback   { get; set; }
    public        Action?                       HideLoadingCallback   { get; set; }
    public        Action<string>?               SetLoadingMessage     { get; set; }
    public        Action<long?, long, double?>? ReportLoadingProgress { get; set; }
    public static string                        OnlineHash            { get; private set; } = string.Empty;
    public static string                        Version               { get; private set; } = string.Empty;

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
    private readonly string?       githubToken;

    private readonly HttpClient httpClient;
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
        httpClient          = XLHttpClientFactory.Create(TimeSpan.FromSeconds(10), 50, DecompressionMethods.All);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");
        if (!string.IsNullOrWhiteSpace(this.githubToken))
            httpClient.DefaultRequestHeaders.Authorization = new("Bearer", this.githubToken);
    }

    public void Run()
    {
        Log.Information("[DUPDATE] 启动 Dalamud 更新器中...");
        State = DownloadState.Unknown;

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
                    }
                }

                State = isUpdated ? DownloadState.Done : DownloadState.NoIntegrity;
            }
        );
    }

    #region Steps

    private async Task UpdateDalamud()
    {
        try
        {
            Log.Information("[DUPDATE] 开始 Dalamud 更新进程");

            await InitVersionInfoAsync();
            var paths                  = PreparePaths();
            var currentVersionVerified = await UpdateDalamudCoreAsync(paths.addonPath, paths.currentVersionPath);
            await UpdateRuntimeAsync();
            await UpdateAssetsAsync();

            if (!currentVersionVerified && !CheckDalamudIntegrity(paths.currentVersionPath))
                throw new DalamudIntegrityException("完整性验证最终失败");

            Runner = new FileInfo(Path.Combine(paths.currentVersionPath.FullName, "Dalamud.Injector.exe"));
            SetLoadingDetail("正在启动...");
            ReportLoadingProgressCore(null, 0, null);

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

    private async Task UpdateRuntimeAsync()
    {
        await DotNetRuntimeManager.EnsureRuntimeAsync(Runtime, runtimeVersion, "win-x64", ".NET 运行时", SetLoadingDetail, ReportLoadingProgressCore).ConfigureAwait(false);
    }

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
        string                     manifestUrl
    )
    {
        var       manifestJson = await httpClient.GetStringAsync(manifestUrl).ConfigureAwait(false);
        using var manifestDoc  = JsonDocument.Parse(manifestJson);
        var       manifest     = ReadDalamudManifest(manifestDoc.RootElement);

        if (!assets.TryGetValue(manifest.HashesAsset, out var hashesUrl))
            throw new InvalidDataException("[DUPDATE] Dalamud 文件清单缺少 hashes.json 资产");

        var plan = BuildDalamudUpdatePlan(addonPath, devPath, assets, manifest.HashesAsset, manifest.Files);

        var remoteFileCount = plan.DownloadFiles.Count;

        if (remoteFileCount * 2 > manifest.Files.Count)
        {
            Log.Information("[DUPDATE] 需远程下载文件数超过一半, 改用完整包更新: {DownloadCount}/{TotalCount}", remoteFileCount, manifest.Files.Count);
            return false;
        }

        foreach (var file in plan.CopyFiles)
        {
            Log.Verbose("[DUPDATE] 从 dev 目录复用 Dalamud 文件: {Path}", Path.GetRelativePath(addonPath.FullName, file.TargetPath));
            Directory.CreateDirectory(Path.GetDirectoryName(file.TargetPath)!);
            File.Copy(file.SourcePath, file.TargetPath, true);
        }

        plan.DownloadFiles.Add((hashesUrl, GetReleaseFilePath(addonPath, manifest.HashesAsset), OnlineHash));
        await DownloadVerifiedFiles(plan.DownloadFiles).ConfigureAwait(false);
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

    public async Task GetDalamudVersionInfoAsync()
    {
        try
        {
            runtimeVersion = await DotNetRuntimeManager.GetLatestVersionAsync(httpClient).ConfigureAwait(false);

            Log.Information("[DUPDATE] 获取到远端 Dalamud 运行时版本: {0}", runtimeVersion);

            var response = await httpClient.GetAsync(Links.DALAMUD_RELEASE_INFO_URL);
            response.EnsureSuccessStatusCode();

            var       json    = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(json);

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
                    fileResponse.EnsureSuccessStatusCode();
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
        try
        {
            var response = await httpClient.GetAsync(Links.DALAMUD_RELEASE_INFO_URL);
            response.EnsureSuccessStatusCode();

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

                var incrementalUpdated = await TryUpdateDalamudIncrementally(addonPath, devPath, assets, manifestUrl).ConfigureAwait(false);

                if (!incrementalUpdated)
                    await DownloadDalamudPackage(addonPath, assets).ConfigureAwait(false);
            }
            else
            {
                Log.Information(hasLocalSeed ? "[DUPDATE] 未发现 Dalamud 文件清单, 开始下载完整包" : "[DUPDATE] 未发现可复用 Dalamud 本地基线, 开始下载完整包");
                await DownloadDalamudPackage(addonPath, assets).ConfigureAwait(false);
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

    private async Task DownloadDalamudPackage(DirectoryInfo addonPath, Dictionary<string, string> assets)
    {
        if (!assets.TryGetValue(RELEASE_PACKAGE_ASSET, out var downloadUrl))
            throw new InvalidDataException("[DUPDATE] 未找到 Dalamud 完整包");

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

    private async Task DownloadVerifiedFiles(IReadOnlyList<(string Url, string Path, string Hash)> files)
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
            await DownloadFiles(downloads).ConfigureAwait(false);

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

    private void SetLoadingDetail(string message) =>
        SetLoadingMessage?.Invoke(message);

    public void ShowLoading() =>
        ShowLoadingCallback?.Invoke();

    public void HideLoading() =>
        HideLoadingCallback?.Invoke();

    private void ReportLoadingProgressCore(long? size, long downloaded, double? progress) =>
        ReportLoadingProgress?.Invoke(size, downloaded, progress);

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

    #endregion
}
