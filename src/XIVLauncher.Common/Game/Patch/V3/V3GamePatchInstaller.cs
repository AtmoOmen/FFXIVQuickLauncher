using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Patching.SdoFileDownload;

namespace XIVLauncher.Common.Game.Patch.V3;

public sealed class V3GamePatchInstaller : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        NumberHandling           = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient client = new();

    public V3GamePatchInstaller()
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(USER_AGENT);
    }

    public void Dispose() =>
        client.Dispose();

    public async Task InstallAsync
    (
        V3GameUpdatePlan          plan,
        DirectoryInfo             gamePath,
        DirectoryInfo             patchPath,
        ISdoFileDownloadInstaller installer,
        bool                      keepPatches,
        TimeSpan                  progressUpdateInterval,
        IProgress<V3GamePatchProgress>? progress,
        CancellationToken         cancellationToken
    )
    {
        var packageRoot = Path.Combine(patchPath.FullName, "v3");
        Directory.CreateDirectory(packageRoot);

        var sourceFilesText = await client.GetStringAsync(SdoInfos.CLIENT_ALL_FILES_LIST_URL, cancellationToken).ConfigureAwait(false);
        var sourceFileLines = sourceFilesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var sourceFiles     = new Dictionary<string, (long Size, string Md5)>(StringComparer.OrdinalIgnoreCase);
        var sourceVersion   = string.Empty;

        if (sourceFileLines.Length > 0)
        {
            var headerParts = sourceFileLines[0].Split('|');
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

        for (var packageIndex = 0; packageIndex < plan.Packages.Count; packageIndex++)
        {
            var package = plan.Packages[packageIndex];
            var packageSourceFiles = string.Equals(sourceVersion, package.From, StringComparison.Ordinal) ? sourceFiles : null;
            var packageName = string.IsNullOrWhiteSpace(package.Name)
                                  ? $"{package.From}-{package.To}"
                                  : package.Name;

            foreach (var invalidChar in Path.GetInvalidFileNameChars())
                packageName = packageName.Replace(invalidChar, '_');

            var packageDirectory = Path.Combine(packageRoot, packageName);
            Directory.CreateDirectory(packageDirectory);

            progress?.Report
            (
                new()
                {
                    PhaseText   = $"正在获取更新清单 {packageIndex + 1}/{plan.Packages.Count}",
                    CurrentFile = package.FileListUrl
                }
            );

            var fileListJson = await client.GetStringAsync(SdoCdnUrlSigner.Sign(new Uri(new Uri(plan.BaseUrl.TrimEnd('/') + "/"), package.FileListUrl.TrimStart('/'))), cancellationToken).ConfigureAwait(false);
            var fileList = JsonSerializer.Deserialize<V3GamePackageFileList>(fileListJson, SerializerOptions)
                           ?? throw new InvalidDataException("未能解析 V3 更新清单");

            if (fileList.FileList.Count == 0)
                throw new InvalidDataException("V3 更新清单为空");

            var downloadBaseUrl = string.IsNullOrWhiteSpace(fileList.BaseUrl) ? plan.BaseUrl : fileList.BaseUrl;
            var totalDownload   = fileList.FileList.Sum(entry => entry.Size);
            var downloaded      = 0L;
            var packageFiles    = new List<string>(fileList.FileList.Count);

            foreach (var entry in fileList.FileList)
            {
                var fileName = Path.GetFileName(entry.Path.Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = Path.GetFileName(entry.Url.Replace('\\', '/'));

                var localFilePath = Path.Combine(packageDirectory, fileName);
                packageFiles.Add(localFilePath);

                if (File.Exists(localFilePath) && await IsFileValidAsync(localFilePath, entry.Md5, cancellationToken).ConfigureAwait(false))
                {
                    downloaded += entry.Size;
                    continue;
                }

                await DownloadFileAsync
                    (
                        SdoCdnUrlSigner.Sign(new Uri(new Uri(downloadBaseUrl.TrimEnd('/') + "/"), entry.Url.TrimStart('/'))),
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

            List<KeyValuePair<string, string>>? deltaMap = null;
            var deltaEntries    = new Dictionary<string, (int PackageFileIndex, string EntryName)>(StringComparer.OrdinalIgnoreCase);
            var packageArchives = new List<ZipArchive>(packageFiles.Count);

            try
            {
                for (var packageFileIndex = 0; packageFileIndex < packageFiles.Count; packageFileIndex++)
                {
                    var archive = ZipFile.OpenRead(packageFiles[packageFileIndex]);
                    packageArchives.Add(archive);

                    if (deltaMap == null)
                    {
                        var mapEntry = archive.GetEntry("patch_delta_direct.dat");
                        if (mapEntry != null)
                        {
                            await using var stream   = mapEntry.Open();
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

                for (var deltaIndex = 0; deltaIndex < deltaMap.Count; deltaIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var targetRelativePath = deltaMap[deltaIndex].Key;
                    var deltaEntryPath     = deltaMap[deltaIndex].Value;
                    var normalizedTargetPath = targetRelativePath.TrimStart('\\', '/').Replace('\\', '/');
                    if (!deltaEntries.TryGetValue(deltaEntryPath.Replace('\\', '/'), out var entryInfo))
                        throw new FileNotFoundException($"更新包缺少差分文件: {deltaEntryPath}");

                    var deltaEntry = packageArchives[entryInfo.PackageFileIndex].GetEntry(entryInfo.EntryName);

                    if (deltaEntry == null)
                        throw new FileNotFoundException($"更新包缺少差分文件: {deltaEntryPath}");

                    var targetPath = Path.Combine(gamePath.FullName, targetRelativePath.Replace('\\', Path.DirectorySeparatorChar));
                    if (!File.Exists(targetPath))
                        throw new FileNotFoundException($"缺少待更新文件: {targetRelativePath}");

                    if (packageSourceFiles != null)
                    {
                        if (!packageSourceFiles.TryGetValue(normalizedTargetPath, out var sourceFile))
                            throw new InvalidDataException($"完整性清单缺少更新源文件: {targetRelativePath}");

                        var targetInfo = new FileInfo(targetPath);
                        if (targetInfo.Length != sourceFile.Size || !await IsFileValidAsync(targetPath, sourceFile.Md5, cancellationToken).ConfigureAwait(false))
                            throw new InvalidDataException($"当前文件状态与更新包起点不一致: {targetRelativePath}");
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

                    await using (var deltaSource = deltaEntry.Open())
                    await using (var deltaTarget = new FileStream(deltaFilePath, FileMode.Create, FileAccess.Write, FileShare.None, FILE_STREAM_BUFFER_SIZE, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        await deltaSource.CopyToAsync(deltaTarget, cancellationToken).ConfigureAwait(false);

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

                    var expectedTargetMd5  = string.Empty;
                    var expectedTargetSize = -1L;
                    if (string.Equals(normalizedTargetPath, "game/ffxivgame.ver", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(plan.TargetGameVersion))
                    {
                        var targetVersionBytes = Encoding.ASCII.GetBytes(plan.TargetGameVersion);
                        expectedTargetMd5  = Convert.ToHexString(MD5.HashData(targetVersionBytes));
                        expectedTargetSize = targetVersionBytes.Length;
                    }

                    await installer.ApplyVcdiff(targetPath, deltaFilePath, targetPath, expectedTargetMd5, expectedTargetSize, cancellationToken).ConfigureAwait(false);
                    File.Delete(deltaFilePath);

                    applied++;
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
                Directory.Delete(packageDirectory, true);
        }

        progress?.Report(new() { PhaseText = "正在写入版本信息", CurrentFile = "LocalVersion3.xml" });
        var productName = $"zone{SdoInfos.APP_ID}_{SdoInfos.BRANCH_ID}_v3";
        var localVersionPayload = JsonSerializer.Serialize
        (
            new
            {
                product_name = productName,
                version = new
                {
                    v    = plan.TargetDataVersion,
                    view = plan.TargetViewVersion
                }
            },
            SerializerOptions
        );
        var localVersion = $"""<?xmlversion="1.0"encoding="utf-8"?><Root><{productName}>{localVersionPayload}</{productName}></Root>""";
        await installer.WriteAllText(Path.Combine(gamePath.FullName, "LocalVersion3.xml"), localVersion, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadFileAsync
    (
        Uri                       sourceUrl,
        string                    targetPath,
        string                    expectedMd5,
        long                      completedBytes,
        long                      totalBytes,
        TimeSpan                  progressUpdateInterval,
        IProgress<V3GamePatchProgress>? progress,
        CancellationToken         cancellationToken
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException());

        var tempPath = string.Concat(targetPath, TEMP_EXTENSION);
        var complete = false;

        try
        {
            using var response = await client.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, FILE_STREAM_BUFFER_SIZE, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var             buffer = new byte[FILE_STREAM_BUFFER_SIZE];
                var             fileDownloaded = 0L;
                var             lastProgress   = 0L;
                var             lastTicks      = Stopwatch.GetTimestamp();
                var             minTicks       = Stopwatch.Frequency * Math.Max(1, (int)progressUpdateInterval.TotalMilliseconds) / 1000;

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

    private static async Task<bool> IsFileValidAsync(string filePath, string expectedMd5, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedMd5))
            return true;

        await using var stream = File.OpenRead(filePath);
        var             hash   = await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expectedMd5, StringComparison.OrdinalIgnoreCase);
    }

    #region Constants

    private const int FILE_STREAM_BUFFER_SIZE = 131072;
    private const string TEMP_EXTENSION = ".tmp";
    private const string USER_AGENT = "FF14v3autopatch";

    #endregion
}
