using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.GamePatchV3.Models;

namespace XIVLauncher.GamePatchV3.Update;

public sealed class GamePatchInstaller : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        NumberHandling              = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient client = new();

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
        var sourceFiles     = new Dictionary<string, (long Size, string Md5)>(StringComparer.OrdinalIgnoreCase);
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

            var filePath = lineParts[0].TrimStart('\\', '/').Replace('\\', '/');
            if (!filePath.StartsWith("game/", StringComparison.OrdinalIgnoreCase) || !long.TryParse(lineParts[1], out var fileSize))
                continue;

            sourceFiles[filePath] = (fileSize, lineParts[2]);
        }

        if (sourceFiles.Count == 0)
            throw new InvalidDataException("未能解析 V3 完整性清单");

        Log.Information("[V3Patch] 完整性清单解析完成, 版本 {SourceVersion}, 文件数 {FileCount}", sourceVersion, sourceFiles.Count);

        for (var packageIndex = 0; packageIndex < plan.Packages.Count; packageIndex++)
        {
            var package            = plan.Packages[packageIndex];
            var packageSourceFiles = string.Equals(sourceVersion, package.From, StringComparison.Ordinal) ? sourceFiles : null;
            var packageTargetFiles = string.Equals(sourceVersion, package.To,   StringComparison.Ordinal) ? sourceFiles : null;
            var packageName = string.IsNullOrWhiteSpace(package.Name)
                                  ? $"{package.From}-{package.To}"
                                  : package.Name;

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

            var fileListJson = await client.GetStringAsync(CDNLinkSigner.Sign(new Uri(new Uri(plan.BaseUrl.TrimEnd('/') + "/"), package.FileListUrl.TrimStart('/'))), cancellationToken).ConfigureAwait
                                   (false);
            var fileList = JsonSerializer.Deserialize<GamePackageFileList>(fileListJson, SerializerOptions)
                           ?? throw new InvalidDataException("未能解析 V3 更新清单");

            if (fileList.FileList.Count == 0)
                throw new InvalidDataException("V3 更新清单为空");

            var downloadBaseUrl = string.IsNullOrWhiteSpace(fileList.BaseUrl) ? plan.BaseUrl : fileList.BaseUrl;
            var totalDownload   = fileList.FileList.Sum(entry => entry.Size);
            var downloaded      = 0L;
            var packageFiles    = new List<string>(fileList.FileList.Count);

            Log.Information("[V3Patch] 更新包清单解析完成, 文件数 {FileCount}, 下载大小 {TotalDownload}", fileList.FileList.Count, totalDownload);

            foreach (var entry in fileList.FileList)
            {
                var fileName = Path.GetFileName(entry.Path.Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = Path.GetFileName(entry.Url.Replace('\\', '/'));

                var localFilePath = Path.Combine(packageDirectory, fileName);
                packageFiles.Add(localFilePath);

                if (File.Exists(localFilePath) && await IsFileValidAsync(localFilePath, entry.Md5, cancellationToken).ConfigureAwait(false))
                {
                    Log.Information("[V3Patch] 更新包文件已存在且校验通过 {FileName}, 大小 {Size}", fileName, entry.Size);
                    downloaded += entry.Size;
                    continue;
                }

                await DownloadFileAsync
                    (
                        CDNLinkSigner.Sign(new Uri(new Uri(downloadBaseUrl.TrimEnd('/') + "/"), entry.Url.TrimStart('/'))),
                        localFilePath,
                        entry.Md5,
                        downloaded,
                        totalDownload,
                        progressUpdateInterval,
                        progress,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                downloaded += entry.Size;
            }

            List<KeyValuePair<string, string>>? deltaMap        = null;
            var                                 deltaEntries    = new Dictionary<string, (int PackageFileIndex, string EntryName)>(StringComparer.OrdinalIgnoreCase);
            var                                 packageArchives = new List<ZipArchive>(packageFiles.Count);

            try
            {
                for (var packageFileIndex = 0; packageFileIndex < packageFiles.Count; packageFileIndex++)
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
                                               .Select(element => new KeyValuePair<string, string>(element.Attribute("Key")?.Value ?? string.Empty, element.Attribute("Value")?.Value ?? string.Empty))
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

                    var targetRelativePath   = deltaMap[deltaIndex].Key;
                    var deltaEntryPath       = deltaMap[deltaIndex].Value;
                    var normalizedTargetPath = targetRelativePath.TrimStart('\\', '/').Replace('\\', '/');
                    if (!deltaEntries.TryGetValue(deltaEntryPath.Replace('\\', '/'), out var entryInfo))
                        throw new FileNotFoundException($"更新包缺少差分文件: {deltaEntryPath}");

                    var deltaEntry = packageArchives[entryInfo.PackageFileIndex].GetEntry(entryInfo.EntryName);

                    if (deltaEntry == null)
                        throw new FileNotFoundException($"更新包缺少差分文件: {deltaEntryPath}");

                    var targetPath = Path.Combine(gamePath.FullName, targetRelativePath.Replace('\\', Path.DirectorySeparatorChar));
                    if (!File.Exists(targetPath))
                        throw new FileNotFoundException($"缺少待更新文件: {targetRelativePath}");

                    var expectedTargetMd5  = string.Empty;
                    var expectedTargetSize = -1L;

                    if (packageTargetFiles != null && packageTargetFiles.TryGetValue(normalizedTargetPath, out var targetFile))
                    {
                        expectedTargetMd5  = targetFile.Md5;
                        expectedTargetSize = targetFile.Size;
                    }
                    else if (string.Equals(normalizedTargetPath, "game/ffxivgame.ver", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(plan.TargetGameVersion))
                    {
                        var targetVersionBytes = Encoding.ASCII.GetBytes(plan.TargetGameVersion);
                        expectedTargetMd5  = Convert.ToHexString(MD5.HashData(targetVersionBytes));
                        expectedTargetSize = targetVersionBytes.Length;
                    }

                    if (!string.IsNullOrWhiteSpace(expectedTargetMd5))
                    {
                        var targetInfo = new FileInfo(targetPath);

                        if ((expectedTargetSize < 0 || targetInfo.Length == expectedTargetSize) && await IsFileValidAsync(targetPath, expectedTargetMd5, cancellationToken).ConfigureAwait(false))
                        {
                            applied++;
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

                    if (packageSourceFiles != null)
                    {
                        if (!packageSourceFiles.TryGetValue(normalizedTargetPath, out var sourceFile))
                            throw new InvalidDataException($"完整性清单缺少更新源文件: {targetRelativePath}");

                        var targetInfo          = new FileInfo(targetPath);
                        var sourceValidateTicks = Stopwatch.GetTimestamp();
                        var sourceFileSize      = sourceFile.Size;
                        var sourceValidateProgress = new Progress<long>
                        (value => progress?.Report
                         (
                             new()
                             {
                                 PhaseText      = $"正在校验更新文件 {packageIndex + 1}/{plan.Packages.Count}",
                                 CurrentFile    = targetRelativePath,
                                 Progress       = value,
                                 Total          = sourceFileSize,
                                 IsByteProgress = true
                             }
                         )
                        );

                        Log.Information("[V3Patch] 正在校验源文件 {Path}, 大小 {Size}", targetRelativePath, sourceFile.Size);

                        if (targetInfo.Length != sourceFile.Size
                            || !await IsFileValidAsync(targetPath, sourceFile.Md5, cancellationToken, sourceValidateProgress, progressUpdateInterval).ConfigureAwait(false))
                        {
                            Log.Warning("[V3Patch] 源文件校验失败 {Path}, 实际大小 {ActualSize}, 期望大小 {ExpectedSize}", targetRelativePath, targetInfo.Length, sourceFile.Size);
                            throw new InvalidDataException($"当前文件状态与更新包起点不一致: {targetRelativePath}");
                        }

                        Log.Information("[V3Patch] 源文件校验完成 {Path}, 耗时 {ElapsedMs} ms", targetRelativePath, Stopwatch.GetElapsedTime(sourceValidateTicks).TotalMilliseconds);
                    }

                    var deltaDirectory = Path.Combine(packageDirectory, "delta");
                    Directory.CreateDirectory(deltaDirectory);

                    var deltaFilePath = Path.Combine(deltaDirectory, $"{entryInfo.PackageFileIndex}_{deltaIndex}.delta");
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

                    var extractTicks     = Stopwatch.GetTimestamp();
                    var extracted        = 0L;
                    var lastExtracted    = 0L;
                    var lastExtractTicks = Stopwatch.GetTimestamp();
                    var minExtractTicks  = Stopwatch.Frequency * Math.Max(1, (int)progressUpdateInterval.TotalMilliseconds) / 1000;
                    var deltaEntryLength = deltaEntry.Length;
                    var extractBuffer    = new byte[FILE_STREAM_BUFFER_SIZE];

                    Log.Information("[V3Patch] 正在解压差分 {Entry}, 目标 {Path}, 大小 {Size}", deltaEntryPath, targetRelativePath, deltaEntryLength);

                    await using (var deltaSource = await deltaEntry.OpenAsync(cancellationToken))
                    await using (var deltaTarget = new FileStream
                                     (deltaFilePath, FileMode.Create, FileAccess.Write, FileShare.None, FILE_STREAM_BUFFER_SIZE, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        while (true)
                        {
                            var read = await deltaSource.ReadAsync(extractBuffer, cancellationToken).ConfigureAwait(false);
                            if (read == 0)
                                break;

                            await deltaTarget.WriteAsync(extractBuffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            extracted += read;

                            var ticks = Stopwatch.GetTimestamp();
                            if (ticks - lastExtractTicks < minExtractTicks)
                                continue;

                            var speed = (extracted - lastExtracted) * Stopwatch.Frequency / Math.Max(1, ticks - lastExtractTicks);
                            progress?.Report
                            (
                                new()
                                {
                                    PhaseText      = $"正在解压更新文件 {packageIndex + 1}/{plan.Packages.Count}",
                                    CurrentFile    = targetRelativePath,
                                    Progress       = extracted,
                                    Total          = deltaEntryLength,
                                    Speed          = speed,
                                    IsByteProgress = true
                                }
                            );

                            lastExtracted    = extracted;
                            lastExtractTicks = ticks;
                        }

                        await deltaTarget.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    Log.Information("[V3Patch] 差分解压完成 {Path}, 耗时 {ElapsedMs} ms", targetRelativePath, Stopwatch.GetElapsedTime(extractTicks).TotalMilliseconds);

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

                    var deltaProgress = new Progress<(long Progress, long Total)>
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
                        await vcdiffClient.ApplyVcdiff(targetPath, deltaFilePath, targetPath, expectedTargetMd5, expectedTargetSize, deltaProgress, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[V3Patch] 差分合并失败, 尝试下载完整目标文件 {Path}, 期望 MD5 {ExpectedMd5}, 期望大小 {ExpectedSize}", targetRelativePath, expectedTargetMd5, expectedTargetSize);

                        if (string.IsNullOrWhiteSpace(sourceBaseUrl))
                        {
                            Log.Error("[V3Patch] 缺少源文件下载地址, 无法下载完整文件 {Path}", targetRelativePath);
                            throw;
                        }

                        if (!string.IsNullOrWhiteSpace(expectedTargetMd5))
                        {
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

                            var sdoTargetPath = "\\" + normalizedTargetPath.Replace('/', '\\');
                            var targetIntegrity = new IntegrityCheckResult
                            {
                                BaseUrl     = sourceBaseUrl,
                                DataVersion = sourceVersion,
                                Hashes      = { [sdoTargetPath] = expectedTargetMd5 },
                                Sizes       = { [sdoTargetPath] = (ulong)expectedTargetSize }
                            };

                            using var sdoDownloader = new SdoFileDownloader();
                            sdoDownloader.ProgressReportInterval = (int)progressUpdateInterval.TotalMilliseconds;
                            sdoDownloader.ConstructFromRemoteIntegrity(targetIntegrity);
                            await sdoDownloader.VerifyFiles(gamePath.FullName, false, 1, cancellationToken).ConfigureAwait(false);
                            sdoDownloader.QueueInstall(0, sdoTargetPath);
                            await sdoDownloader.Install(gamePath.FullName, 1, cancellationToken).ConfigureAwait(false);

                            var repairedInfo = new FileInfo(targetPath);
                            if (repairedInfo.Length != expectedTargetSize || !await IsFileValidAsync(targetPath, expectedTargetMd5, cancellationToken).ConfigureAwait(false))
                                throw new InvalidDataException($"完整目标文件修复校验失败: {targetRelativePath}", ex);

                            Log.Information("[V3Patch] 完整目标文件修复完成 {Path}", targetRelativePath);
                        }
                        else
                        {
                            var directoryPath = Path.GetDirectoryName(normalizedTargetPath.Replace('/', '\\')) ?? string.Empty;
                            var fileKeyInput  = $"{SdoInfos.APP_ID}_{sourceVersion}_\\{normalizedTargetPath.Replace('/', '\\')}";
                            var fileKeyBytes  = MD5.HashData(Encoding.Unicode.GetBytes(fileKeyInput));
                            var fileKeyHex    = Convert.ToHexString(fileKeyBytes).ToLowerInvariant();
                            var downloadUri   = new Uri($"{sourceBaseUrl.TrimEnd('/')}/{directoryPath.Replace('\\', '/')}/{fileKeyHex}");
                            var signedUri     = CDNLinkSigner.Sign(downloadUri);

                            Log.Information("[V3Patch] 正在直接下载完整文件 {Path}, 地址 {Url}", targetRelativePath, signedUri.GetLeftPart(UriPartial.Path));
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

                            var tempDownloadPath = string.Concat(targetPath, ".fix");

                            try
                            {
                                using var response = await client.GetAsync(signedUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                                response.EnsureSuccessStatusCode();

                                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                                await using var sink = new FileStream
                                    (tempDownloadPath, FileMode.Create, FileAccess.Write, FileShare.None, FILE_STREAM_BUFFER_SIZE, FileOptions.Asynchronous | FileOptions.SequentialScan);
                                await source.CopyToAsync(sink, cancellationToken).ConfigureAwait(false);
                                await sink.FlushAsync(cancellationToken).ConfigureAwait(false);

                                File.Move(tempDownloadPath, targetPath, true);
                            }
                            catch
                            {
                                try
                                {
                                    File.Delete(tempDownloadPath);
                                }
                                catch
                                {
                                }

                                throw;
                            }

                            Log.Information("[V3Patch] 直接下载完整文件完成 {Path}", targetRelativePath);
                        }
                    }

                    File.Delete(deltaFilePath);

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

    private async Task DownloadFileAsync
    (
        Uri                           sourceUrl,
        string                        targetPath,
        string                        expectedMd5,
        long                          completedBytes,
        long                          totalBytes,
        TimeSpan                      progressUpdateInterval,
        IProgress<GamePatchProgress>? progress,
        CancellationToken             cancellationToken
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException());

        var tempPath = string.Concat(targetPath, TEMP_EXTENSION);
        var complete = false;

        try
        {
            var downloadTicks = Stopwatch.GetTimestamp();
            Log.Information("[V3Patch] 开始下载更新包文件 {FileName}, 地址 {Url}, 目标 {TargetPath}, 期望 MD5 {Md5}", Path.GetFileName(targetPath), sourceUrl.GetLeftPart(UriPartial.Path), targetPath, expectedMd5);

            using var response = await client.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, FILE_STREAM_BUFFER_SIZE, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var             buffer = new byte[FILE_STREAM_BUFFER_SIZE];
                var             fileDownloaded = 0L;
                var             lastProgress = 0L;
                var             lastTicks = Stopwatch.GetTimestamp();
                var             minTicks = Stopwatch.Frequency * Math.Max(1, (int)progressUpdateInterval.TotalMilliseconds) / 1000;

                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    fileDownloaded += read;

                    var ticks = Stopwatch.GetTimestamp();
                    if (ticks - lastTicks < minTicks)
                        continue;

                    var speed = (fileDownloaded - lastProgress) * Stopwatch.Frequency / Math.Max(1, ticks - lastTicks);
                    progress?.Report
                    (
                        new()
                        {
                            PhaseText      = "正在下载更新包",
                            CurrentFile    = Path.GetFileName(targetPath),
                            Progress       = completedBytes + fileDownloaded,
                            Total          = totalBytes,
                            Speed          = speed,
                            IsByteProgress = true
                        }
                    );

                    lastProgress = fileDownloaded;
                    lastTicks    = ticks;
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, targetPath, true);

            if (!await IsFileValidAsync(targetPath, expectedMd5, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException($"更新包校验失败: {Path.GetFileName(targetPath)}");

            Log.Information("[V3Patch] 更新包文件下载完成 {FileName}, 耗时 {ElapsedMs} ms", Path.GetFileName(targetPath), Stopwatch.GetElapsedTime(downloadTicks).TotalMilliseconds);
            complete = true;
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
                }
            }
        }
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

    #region Constants

    private const int    FILE_STREAM_BUFFER_SIZE = 131072;
    private const string TEMP_EXTENSION          = ".tmp";
    private const string USER_AGENT              = "FF14v3autopatch";

    #endregion
}
