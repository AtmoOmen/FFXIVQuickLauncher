using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.GamePatchV3.Integrity.Models;
using XIVLauncher.GamePatchV3.Models;
using XIVLauncher.GamePatchV3.Update.Models;

namespace XIVLauncher.GamePatchV3.Update;

public sealed class GamePatchInstaller : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        NumberHandling              = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient client = new()
    {
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionOrLower
    };

    public GamePatchInstaller() =>
        client.DefaultRequestHeaders.UserAgent.ParseAdd(USER_AGENT);

    public void Dispose() =>
        client.Dispose();

    public async Task InstallAsync
    (
        GameUpdatePlan                plan,
        DirectoryInfo                 gamePath,
        DirectoryInfo                 patchPath,
        VcdiffClient                  vcdiffClient,
        bool                          keepPatches,
        TimeSpan                      progressUpdateInterval,
        IProgress<GamePatchProgress>? progress,
        CancellationToken             cancellationToken
    )
    {
        var packageRoot = Path.Combine(patchPath.FullName, "v3");
        Directory.CreateDirectory(packageRoot);

        Log.Information
        (
            "[V3Patch] 开始安装更新, 游戏 {GamePath}, 补丁 {PatchPath}, 包数量 {PackageCount}, 保留补丁 {KeepPatches}",
            gamePath.FullName,
            patchPath.FullName,
            plan.Packages.Count,
            keepPatches
        );

        Log.Information("[V3Patch] 正在获取完整性清单 {Url}", SdoInfos.CLIENT_ALL_FILES_LIST_URL);
        var sourceFilesText = await client.GetStringAsync(SdoInfos.CLIENT_ALL_FILES_LIST_URL, cancellationToken).ConfigureAwait(false);
        var sourceFileLines = sourceFilesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var sourceFiles     = new Dictionary<string, (long Size, string Md5, string DownloadPath)>(StringComparer.OrdinalIgnoreCase);
        var sourceBaseUrl   = string.Empty;
        var sourceVersion   = string.Empty;

        if (sourceFileLines.Length > 0)
        {
            var headerParts = sourceFileLines[0].Split('|');
            if (headerParts.Length >= 1)
                sourceBaseUrl = headerParts[0];

            if (headerParts.Length >= 3)
                sourceVersion = headerParts[2];
        }

        foreach (var line in sourceFileLines.Skip(1))
        {
            var lineParts = line.Split('|');
            if (lineParts.Length < 3)
                continue;

            if (!GamePathNormalizer.TryNormalizeGameRelativePath(lineParts[0], out var gameRelativePath) ||
                !long.TryParse(lineParts[1], out var fileSize)                                           ||
                fileSize < 0)
                continue;

            sourceFiles[gameRelativePath] = (fileSize, lineParts[2], GamePathNormalizer.NormalizeDownloadPath(lineParts[0]));
        }

        if (sourceFiles.Count == 0)
            throw new InvalidDataException("未能解析 V3 完整性清单");

        Log.Information("[V3Patch] 完整性清单解析完成, 版本 {SourceVersion}, 文件数 {FileCount}", sourceVersion, sourceFiles.Count);

        var reachedTargetFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var packageIndex = 0; packageIndex < plan.Packages.Count; packageIndex++)
        {
            var package    = plan.Packages[packageIndex];
            var isFinalHop = string.Equals(sourceVersion, package.To, StringComparison.Ordinal);
            var packageName = string.IsNullOrWhiteSpace(package.Name) ?
                                  $"{package.From}-{package.To}" :
                                  package.Name;

            foreach (var invalidChar in Path.GetInvalidFileNameChars())
                packageName = packageName.Replace(invalidChar, '_');

            var packageDirectory = Path.Combine(packageRoot, packageName);
            Directory.CreateDirectory(packageDirectory);

            Log.Information
            (
                "[V3Patch] 开始处理更新包 {PackageIndex}/{PackageCount}, 名称 {PackageName}, 版本 {FromVersion} -> {ToVersion}, 清单 {FileListUrl}",
                packageIndex + 1,
                plan.Packages.Count,
                packageName,
                package.From,
                package.To,
                package.FileListUrl
            );

            progress?.Report
            (
                new()
                {
                    PhaseText   = $"正在获取更新清单 {packageIndex + 1}/{plan.Packages.Count}",
                    CurrentFile = package.FileListUrl
                }
            );

            var fileListUrls = BuildDownloadUrls(package.FileListUrl, plan.BaseUrl, plan.BackupBaseUrl);
            var fileListJson = await DownloadStringAsync(fileListUrls, cancellationToken).ConfigureAwait(false);
            var fileList     = JsonSerializer.Deserialize<GamePackageFileList>(fileListJson, SerializerOptions) ?? throw new InvalidDataException("未能解析 V3 更新清单");

            if (fileList.FileList.Count == 0)
                throw new InvalidDataException("V3 更新清单为空");

            var  packageFiles  = BuildPackageFilePaths(packageDirectory, fileList.FileList);
            long totalDownload = 0;

            foreach (var entry in fileList.FileList)
            {
                if (entry.Size < 0 || long.MaxValue - totalDownload < entry.Size)
                    throw new InvalidDataException($"V3 更新包文件大小无效: {entry.Path}");

                totalDownload += entry.Size;
            }

            Log.Information("[V3Patch] 更新包清单解析完成, 文件数 {FileCount}, 下载大小 {TotalDownload}", fileList.FileList.Count, totalDownload);

            long downloaded              = 0;
            long networkDownloaded       = 0;
            long lastDownloadReportTicks = 0;
            var  downloadStartTicks      = Stopwatch.GetTimestamp();
            var  minDownloadReportTicks  = Stopwatch.Frequency * Math.Max(1, (int)progressUpdateInterval.TotalMilliseconds) / 1000;

            void ReportDownloadProgress
            (
                string fileName,
                long   byteDelta,
                bool   force
            )
            {
                var current = Interlocked.Add(ref downloaded, byteDelta);
                if (byteDelta != 0)
                    Interlocked.Add(ref networkDownloaded, byteDelta);

                var ticks    = Stopwatch.GetTimestamp();
                var previous = Interlocked.Read(ref lastDownloadReportTicks);
                if (!force && ticks - previous < minDownloadReportTicks)
                    return;

                if (!force && Interlocked.CompareExchange(ref lastDownloadReportTicks, ticks, previous) != previous)
                    return;

                var elapsedTicks = ticks - downloadStartTicks;
                var speed = elapsedTicks <= 0 ?
                                0 :
                                Math.Max(0, Interlocked.Read(ref networkDownloaded)) * Stopwatch.Frequency / elapsedTicks;
                progress?.Report
                (
                    new()
                    {
                        PhaseText      = "正在下载更新包",
                        CurrentFile    = fileName,
                        Progress       = Math.Clamp(current, 0, totalDownload),
                        Total          = totalDownload,
                        Speed          = speed,
                        IsByteProgress = true
                    }
                );
            }

            await Parallel.ForAsync
            (
                0,
                fileList.FileList.Count,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Math.Max(Environment.ProcessorCount, 1), MAX_PACKAGE_DOWNLOAD_CONCURRENCY),
                    CancellationToken      = cancellationToken
                },
                async (entryIndex, token) =>
                {
                    var entry         = fileList.FileList[entryIndex];
                    var localFilePath = packageFiles[entryIndex];
                    var fileName      = Path.GetFileName(localFilePath);

                    if (File.Exists(localFilePath))
                    {
                        var fileInfo = new FileInfo(localFilePath);

                        if (fileInfo.Length == entry.Size && await IsFileValidAsync(localFilePath, entry.Md5, token).ConfigureAwait(false))
                        {
                            Log.Information("[V3Patch] 更新包文件已存在且校验通过 {FileName}, 大小 {Size}", fileName, entry.Size);
                            Interlocked.Add(ref downloaded, entry.Size);
                            ReportDownloadProgress(fileName, 0, true);
                            return;
                        }
                    }

                    var primaryDownloadBaseUrl = string.IsNullOrWhiteSpace(fileList.BaseUrl) ?
                                                     plan.BaseUrl :
                                                     fileList.BaseUrl;
                    var backupDownloadBaseUrl = string.IsNullOrWhiteSpace(fileList.BackupBaseUrl) ?
                                                    plan.BackupBaseUrl :
                                                    fileList.BackupBaseUrl;
                    var downloadUrls = BuildDownloadUrls
                    (
                        entry.Url,
                        primaryDownloadBaseUrl,
                        backupDownloadBaseUrl,
                        plan.BaseUrl,
                        plan.BackupBaseUrl
                    );

                    await DownloadFileAsync
                        (
                            downloadUrls,
                            localFilePath,
                            entry.Md5,
                            entry.Size,
                            byteDelta => ReportDownloadProgress(fileName, byteDelta, false),
                            token
                        )
                        .ConfigureAwait(false);

                    ReportDownloadProgress(fileName, 0, true);
                }
            ).ConfigureAwait(false);

            List<KeyValuePair<string, string>>? deltaMap        = null;
            var                                 deltaEntries    = new Dictionary<string, (int PackageFileIndex, string EntryName)>(StringComparer.OrdinalIgnoreCase);
            var                                 packageArchives = new List<ZipArchive>(packageFiles.Length);

            try
            {
                for (var packageFileIndex = 0; packageFileIndex < packageFiles.Length; packageFileIndex++)
                {
                    var archive = await ZipFile.OpenReadAsync(packageFiles[packageFileIndex], cancellationToken);
                    packageArchives.Add(archive);

                    if (deltaMap == null)
                    {
                        var mapEntry = archive.GetEntry("patch_delta_direct.dat");

                        if (mapEntry != null)
                        {
                            await using var stream   = await mapEntry.OpenAsync(cancellationToken);
                            var             document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);

                            deltaMap = document.Descendants("DeltaPathSubItem")
                                               .Select
                                               (element => new KeyValuePair<string, string>
                                                    (element.Attribute("Key")?.Value ?? string.Empty, element.Attribute("Value")?.Value ?? string.Empty)
                                               )
                                               .Where(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
                                               .ToList();
                        }
                    }

                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.FullName.EndsWith(".delta", StringComparison.OrdinalIgnoreCase))
                            continue;

                        deltaEntries.TryAdd(entry.FullName.Replace('\\', '/'), (packageFileIndex, entry.FullName));
                    }
                }

                if (deltaMap == null)
                    throw new InvalidDataException("更新包缺少 patch_delta_direct.dat");

                var applyTotal = deltaMap.Count;
                var applied    = 0L;

                Log.Information("[V3Patch] 更新包差分索引解析完成, 差分数 {DeltaCount}, 压缩包数 {ArchiveCount}", applyTotal, packageArchives.Count);

                for (var deltaIndex = 0; deltaIndex < deltaMap.Count; deltaIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var targetRelativePath = deltaMap[deltaIndex].Key;
                    var deltaEntryPath     = deltaMap[deltaIndex].Value;
                    if (!GamePathNormalizer.TryNormalizeGameRelativePath(targetRelativePath, out var gameRelativePath))
                        throw new InvalidDataException($"更新包目标路径无效: {targetRelativePath}");

                    if (!deltaEntries.TryGetValue(deltaEntryPath.Replace('\\', '/'), out var entryInfo))
                        throw new FileNotFoundException($"更新包缺少差分文件: {deltaEntryPath}");

                    var deltaEntry = packageArchives[entryInfo.PackageFileIndex].GetEntry(entryInfo.EntryName);

                    if (deltaEntry == null)
                        throw new FileNotFoundException($"更新包缺少差分文件: {deltaEntryPath}");

                    var targetPath = GamePathNormalizer.CombineWithRootPath(gamePath.FullName, gameRelativePath);
                    if (!File.Exists(targetPath))
                        throw new FileNotFoundException($"缺少待更新文件: {targetRelativePath}");

                    if (reachedTargetFiles.Contains(gameRelativePath))
                    {
                        applied++;
                        Log.Information("[V3Patch] 文件已回退至目标版本, 跳过后续差分 {Path}, 进度 {Applied}/{Total}", targetRelativePath, applied, applyTotal);
                        progress?.Report
                        (
                            new()
                            {
                                PhaseText      = $"正在安装更新文件 {packageIndex + 1}/{plan.Packages.Count}",
                                CurrentFile    = targetRelativePath,
                                Progress       = applied,
                                Total          = applyTotal,
                                StatusText     = $"{applied}/{applyTotal}",
                                IsByteProgress = false
                            }
                        );
                        continue;
                    }

                    var expectedTargetMd5  = string.Empty;
                    var expectedTargetSize = -1L;
                    var targetDownloadPath = GamePathNormalizer.ToCanonicalSdoPathFromGameRelativePath(gameRelativePath);
                    var hasTargetFile      = sourceFiles.TryGetValue(gameRelativePath, out var targetFile);

                    if (hasTargetFile)
                    {
                        expectedTargetMd5  = targetFile.Md5;
                        expectedTargetSize = targetFile.Size;
                        targetDownloadPath = targetFile.DownloadPath;
                    }
                    else if (string.Equals
                                 (gameRelativePath, "game/ffxivgame.ver", StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrWhiteSpace(plan.TargetGameVersion))
                    {
                        var targetVersionBytes = Encoding.ASCII.GetBytes(plan.TargetGameVersion);
                        expectedTargetMd5  = Convert.ToHexString(MD5.HashData(targetVersionBytes));
                        expectedTargetSize = targetVersionBytes.Length;
                    }

                    if (isFinalHop && (expectedTargetSize < 0 || string.IsNullOrWhiteSpace(expectedTargetMd5)))
                        throw new InvalidDataException($"目标完整性清单缺少更新文件: {targetRelativePath}");

                    if (!string.IsNullOrWhiteSpace(expectedTargetMd5))
                    {
                        var targetInfo = new FileInfo(targetPath);

                        if ((expectedTargetSize < 0 || targetInfo.Length == expectedTargetSize) &&
                            await IsFileValidAsync(targetPath, expectedTargetMd5, cancellationToken).ConfigureAwait(false))
                        {
                            applied++;
                            reachedTargetFiles.Add(gameRelativePath);
                            Log.Information("[V3Patch] 更新文件已是目标版本, 跳过 {Path}, 进度 {Applied}/{Total}", targetRelativePath, applied, applyTotal);
                            progress?.Report
                            (
                                new()
                                {
                                    PhaseText      = $"正在安装更新文件 {packageIndex + 1}/{plan.Packages.Count}",
                                    CurrentFile    = targetRelativePath,
                                    Progress       = applied,
                                    Total          = applyTotal,
                                    StatusText     = $"{applied}/{applyTotal}",
                                    IsByteProgress = false
                                }
                            );
                            continue;
                        }
                    }

                    progress?.Report
                    (
                        new()
                        {
                            PhaseText      = $"正在准备更新文件 {packageIndex + 1}/{plan.Packages.Count}",
                            CurrentFile    = targetRelativePath,
                            Progress       = applied,
                            Total          = applyTotal,
                            StatusText     = $"{applied}/{applyTotal}",
                            IsByteProgress = false
                        }
                    );

                    if (deltaEntry.Length > int.MaxValue)
                        throw new InvalidDataException($"V3 差分文件过大: {deltaEntryPath}");

                    var deltaEntryLength = (int)deltaEntry.Length;
                    var lastExtractTicks = 0L;
                    var minExtractTicks  = Stopwatch.Frequency * Math.Max(1, (int)progressUpdateInterval.TotalMilliseconds) / 1000;
                    var extractionProgress = new InlineProgress<(long Progress, long Total)>
                    (value =>
                        {
                            var ticks = Stopwatch.GetTimestamp();
                            if (value.Progress < value.Total && ticks - lastExtractTicks < minExtractTicks)
                                return;

                            lastExtractTicks = ticks;
                            progress?.Report
                            (
                                new()
                                {
                                    PhaseText      = $"正在解压更新文件 {packageIndex + 1}/{plan.Packages.Count}",
                                    CurrentFile    = targetRelativePath,
                                    Progress       = value.Progress,
                                    Total          = value.Total,
                                    IsByteProgress = true
                                }
                            );
                        }
                    );
                    var deltaProgress = new InlineProgress<(long Progress, long Total)>
                    (value => progress?.Report
                     (
                         new()
                         {
                             PhaseText      = $"正在安装更新文件 {packageIndex + 1}/{plan.Packages.Count}",
                             CurrentFile    = targetRelativePath,
                             Progress       = value.Progress,
                             Total          = value.Total,
                             StatusText     = string.Empty,
                             IsByteProgress = value.Total > 0
                         }
                     )
                    );

                    try
                    {
                        var verifyMd5 = isFinalHop ?
                                            expectedTargetMd5 :
                                            string.Empty;
                        var verifySize = isFinalHop ?
                                             expectedTargetSize :
                                             -1L;
                        await using var deltaSource = await deltaEntry.OpenAsync(cancellationToken);
                        await vcdiffClient.ApplyVcdiff
                                          (
                                              targetPath,
                                              deltaSource,
                                              deltaEntryLength,
                                              targetPath,
                                              verifyMd5,
                                              verifySize,
                                              extractionProgress,
                                              deltaProgress,
                                              cancellationToken
                                          )
                                          .ConfigureAwait(false);
                    }
                    catch (Exception ex) when
                        (!cancellationToken.IsCancellationRequested &&
                         ex is IOException or InvalidDataException or TimeoutException or InvalidOperationException or Win32Exception)
                    {
                        Log.Warning(ex, "[V3Patch] 差分合并失败, 回退下载目标版本完整文件 {Path}", targetRelativePath);

                        if (string.IsNullOrWhiteSpace(sourceBaseUrl) || !hasTargetFile)
                        {
                            Log.Error("[V3Patch] 目标清单缺少文件或缺少下载地址, 无法回退 {Path}", targetRelativePath);
                            throw;
                        }

                        progress?.Report
                        (
                            new()
                            {
                                PhaseText      = $"正在修复更新文件 {packageIndex + 1}/{plan.Packages.Count}",
                                CurrentFile    = targetRelativePath,
                                Progress       = applied,
                                Total          = applyTotal,
                                StatusText     = $"{applied}/{applyTotal}",
                                IsByteProgress = false
                            }
                        );

                        using var fallbackDownloader = new GameFileDownloader();
                        fallbackDownloader.ProgressReportInterval = Math.Max(1, (int)progressUpdateInterval.TotalMilliseconds);
                        fallbackDownloader.Construct
                        (
                            [
                                new IntegrityPathEntry
                                (
                                    0,
                                    targetDownloadPath,
                                    GamePathNormalizer.ToCanonicalSdoPathFromGameRelativePath(gameRelativePath),
                                    gameRelativePath,
                                    gameRelativePath["game/".Length..],
                                    expectedTargetMd5,
                                    (ulong)expectedTargetSize
                                )
                            ],
                            sourceBaseUrl,
                            sourceVersion
                        );

                        await fallbackDownloader.VerifyFiles(gamePath.FullName, false, 1, cancellationToken).ConfigureAwait(false);

                        if (fallbackDownloader.GetBrokenFiles().Count > 0)
                        {
                            fallbackDownloader.QueueInstall(0, targetDownloadPath);
                            await fallbackDownloader.Install(gamePath.FullName, 1, cancellationToken).ConfigureAwait(false);
                        }

                        var repairedInfo = new FileInfo(targetPath);
                        if (repairedInfo.Length != expectedTargetSize ||
                            !await IsFileValidAsync(targetPath, expectedTargetMd5, cancellationToken).ConfigureAwait(false))
                            throw new InvalidDataException($"完整目标文件回退校验失败: {targetRelativePath}", ex);

                        reachedTargetFiles.Add(gameRelativePath);
                        Log.Information("[V3Patch] 完整目标文件回退完成 {Path}", targetRelativePath);
                    }

                    applied++;
                    Log.Information("[V3Patch] 更新文件安装完成 {Path}, 进度 {Applied}/{Total}", targetRelativePath, applied, applyTotal);
                    progress?.Report
                    (
                        new()
                        {
                            PhaseText      = $"正在安装更新文件 {packageIndex + 1}/{plan.Packages.Count}",
                            CurrentFile    = targetRelativePath,
                            Progress       = applied,
                            Total          = applyTotal,
                            StatusText     = $"{applied}/{applyTotal}",
                            IsByteProgress = false
                        }
                    );
                }
            }
            finally
            {
                foreach (var archive in packageArchives)
                    archive.Dispose();
            }

            if (!keepPatches)
            {
                Log.Information("[V3Patch] 删除更新包缓存 {PackageDirectory}", packageDirectory);
                Directory.Delete(packageDirectory, true);
            }

            Log.Information("[V3Patch] 更新包处理完成 {PackageName}", packageName);
        }

        Log.Information("[V3Patch] V3 更新安装流程完成");
    }

    internal static string[] BuildPackageFilePaths
    (
        string                              packageDirectory,
        IReadOnlyList<GamePackageFileEntry> entries
    )
    {
        var paths     = new string[entries.Count];
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
        {
            var entry    = entries[entryIndex];
            var fileName = Path.GetFileName(entry.Path.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = Path.GetFileName(entry.Url.Replace('\\', '/'));

            foreach (var invalidChar in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(invalidChar, '_');

            if (string.IsNullOrWhiteSpace(fileName))
                throw new InvalidDataException($"V3 更新包文件名无效: {entry.Url}");

            var uniqueName = fileName;

            if (!usedNames.Add(uniqueName))
            {
                uniqueName = $"{entryIndex:D4}_{fileName}";
                while (!usedNames.Add(uniqueName))
                    uniqueName = $"{entryIndex:D4}_{uniqueName}";
            }

            paths[entryIndex] = Path.Combine(packageDirectory, uniqueName);
        }

        return paths;
    }

    private static List<Uri> BuildDownloadUrls
    (
        string          relativeUrl,
        params string[] baseUrls
    )
    {
        var urls           = new List<Uri>();
        var seen           = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasAbsoluteUrl = Uri.TryCreate(relativeUrl, UriKind.Absolute, out var absoluteUrl);

        foreach (var baseUrl in baseUrls)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) && !hasAbsoluteUrl)
                continue;

            var sourceUrl = hasAbsoluteUrl ?
                                absoluteUrl! :
                                new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), relativeUrl.TrimStart('/'));
            var signedUrl = CDNLinkSigner.Sign(sourceUrl);
            if (seen.Add(signedUrl.AbsoluteUri))
                urls.Add(signedUrl);
        }

        if (urls.Count == 0 && hasAbsoluteUrl)
            urls.Add(CDNLinkSigner.Sign(absoluteUrl!));

        if (urls.Count == 0)
            throw new InvalidDataException($"V3 下载地址无效: {relativeUrl}");

        return urls;
    }

    private async Task<string> DownloadStringAsync
    (
        IReadOnlyList<Uri> sourceUrls,
        CancellationToken  cancellationToken
    )
    {
        Exception? lastException = null;

        foreach (var sourceUrl in sourceUrls)
        {
            try
            {
                return await client.GetStringAsync(sourceUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when
                (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                lastException = ex;
                Log.Warning(ex, "[V3Patch] 元数据下载失败, 尝试备用地址 {Url}", sourceUrl.GetLeftPart(UriPartial.Path));
            }
        }

        throw new IOException("V3 元数据下载失败", lastException);
    }

    private async Task DownloadFileAsync
    (
        IReadOnlyList<Uri> sourceUrls,
        string             targetPath,
        string             expectedMd5,
        long               expectedSize,
        Action<long>       reportProgress,
        CancellationToken  cancellationToken
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException());

        var        tempPath      = string.Concat(targetPath, TEMP_EXTENSION);
        Exception? lastException = null;

        foreach (var sourceUrl in sourceUrls)
        {
            var complete       = false;
            var fileDownloaded = 0L;

            try
            {
                var downloadTicks = Stopwatch.GetTimestamp();
                Log.Information
                (
                    "[V3Patch] 开始下载更新包文件 {FileName}, 地址 {Url}, 目标 {TargetPath}, 期望 MD5 {Md5}",
                    Path.GetFileName(targetPath),
                    sourceUrl.GetLeftPart(UriPartial.Path),
                    targetPath,
                    expectedMd5
                );

                using var response = await client.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value != expectedSize)
                    throw new InvalidDataException($"更新包大小不符: {Path.GetFileName(targetPath)}, 期望 {expectedSize}, 实际 {contentLength.Value}");

                var buffer = ArrayPool<byte>.Shared.Rent(FILE_STREAM_BUFFER_SIZE);

                try
                {
                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var target = new FileStream
                    (
                        tempPath,
                        new FileStreamOptions
                        {
                            Mode              = FileMode.Create,
                            Access            = FileAccess.Write,
                            Share             = FileShare.None,
                            BufferSize        = FILE_STREAM_BUFFER_SIZE,
                            Options           = FileOptions.Asynchronous | FileOptions.SequentialScan,
                            PreallocationSize = expectedSize
                        }
                    );
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, FILE_STREAM_BUFFER_SIZE), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            break;

                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer.AsSpan(0, read));
                        fileDownloaded += read;
                        reportProgress(read);
                    }

                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (fileDownloaded != expectedSize)
                        throw new InvalidDataException($"更新包大小不符: {Path.GetFileName(targetPath)}, 期望 {expectedSize}, 实际 {fileDownloaded}");

                    var actualMd5 = Convert.ToHexString(hash.GetHashAndReset());
                    if (!string.IsNullOrWhiteSpace(expectedMd5) && !string.Equals(actualMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"更新包校验失败: {Path.GetFileName(targetPath)}");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                File.Move(tempPath, targetPath, true);
                Log.Information
                    ("[V3Patch] 更新包文件下载完成 {FileName}, 耗时 {ElapsedMs} ms", Path.GetFileName(targetPath), Stopwatch.GetElapsedTime(downloadTicks).TotalMilliseconds);
                complete = true;
                return;
            }
            catch (Exception ex) when
                (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                lastException = ex;
                if (fileDownloaded != 0)
                    reportProgress(-fileDownloaded);

                Log.Warning(ex, "[V3Patch] 更新包文件下载失败, 尝试备用地址 {Url}", sourceUrl.GetLeftPart(UriPartial.Path));
            }
            finally
            {
                if (!complete)
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Warning(ex, "[V3Patch] 无法删除更新包临时文件 {Path}", tempPath);
                    }
                }
            }
        }

        throw new IOException($"更新包文件下载失败: {Path.GetFileName(targetPath)}", lastException);
    }

    private static async Task<bool> IsFileValidAsync
    (
        string            filePath,
        string            expectedMd5,
        CancellationToken cancellationToken,
        IProgress<long>?  progress               = null,
        TimeSpan          progressUpdateInterval = default
    )
    {
        if (string.IsNullOrWhiteSpace(expectedMd5))
            return true;

        await using var stream = File.OpenRead(filePath);

        if (progress == null)
        {
            var directHash = await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(directHash), expectedMd5, StringComparison.OrdinalIgnoreCase);
        }

        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var       buffer          = new byte[FILE_STREAM_BUFFER_SIZE];
        var       readTotal       = 0L;
        var       lastTicks       = Stopwatch.GetTimestamp();
        var       minTicks        = Stopwatch.Frequency * Math.Max(1, (int)progressUpdateInterval.TotalMilliseconds) / 1000;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            incrementalHash.AppendData(buffer.AsSpan(0, read));
            readTotal += read;

            var ticks = Stopwatch.GetTimestamp();
            if (ticks - lastTicks < minTicks)
                continue;

            progress.Report(readTotal);
            lastTicks = ticks;
        }

        progress.Report(readTotal);
        var incrementalFileHash = incrementalHash.GetHashAndReset();
        return string.Equals(Convert.ToHexString(incrementalFileHash), expectedMd5, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class InlineProgress<T>
    (
        Action<T> callback
    ) : IProgress<T>
    {
        public void Report
        (
            T value
        ) =>
            callback(value);
    }

    #region Constants

    private const int    FILE_STREAM_BUFFER_SIZE          = 131072;
    private const int    MAX_PACKAGE_DOWNLOAD_CONCURRENCY = 4;
    private const string TEMP_EXTENSION                   = ".tmp";
    private const string USER_AGENT                       = "FF14v3autopatch";

    #endregion
}
