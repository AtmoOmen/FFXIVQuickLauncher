using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Runtime;

public static class DotNetRuntimeManager
{
    public static DirectoryInfo GetRuntimeDirectory(string runtimeIdentifier) =>
        string.Equals(runtimeIdentifier, WIN_X64_RUNTIME_IDENTIFIER, StringComparison.OrdinalIgnoreCase)
            ? new(Path.Combine(Paths.RoamingPath, "runtime"))
            : new(Path.Combine(Paths.RoamingPath, $"runtime-{runtimeIdentifier}"));

    public static async Task<string> GetLatestVersionAsync(HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        using var runtimeResponse = await httpClient.GetAsync(Links.DALAMUD_RUNTIME_INFO_URL, cancellationToken).ConfigureAwait(false);
        runtimeResponse.EnsureSuccessStatusCode();
        var runtimeVersion = await runtimeResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return runtimeVersion.Trim();
    }

    public static async Task<string> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        using var httpClient = XLHttpClientFactory.Create(TimeSpan.FromSeconds(10), 50, DecompressionMethods.All);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");
        return await GetLatestVersionAsync(httpClient, cancellationToken).ConfigureAwait(false);
    }

    public static async Task EnsureRuntimeAsync
    (
        DirectoryInfo                 runtimeDirectory,
        string                        version,
        string                        runtimeIdentifier,
        string                        displayName,
        Action<string>?               setLoadingMessage = null,
        Action<long?, long, double?>? reportProgress    = null,
        CancellationToken             cancellationToken = default
    )
    {
        Log.Information("[Runtime] 开始检查 {DisplayName} {Version} 完整性", displayName, version);

        if (!runtimeDirectory.Exists)
            Directory.CreateDirectory(runtimeDirectory.FullName);

        var versionFile        = new FileInfo(Path.Combine(runtimeDirectory.FullName, "version"));
        var localVersion       = GetLocalRuntimeVersion(versionFile);
        var runtimeNeedsUpdate = localVersion != version;
        var runtimePaths       = GetRequiredPaths(runtimeDirectory, version);

        if (runtimePaths.All(path => path.Exists) && !runtimeNeedsUpdate)
        {
            Log.Information("[Runtime] {DisplayName} 已是最新版本: {Version}", displayName, version);
            return;
        }

        Log.Information("[Runtime] 需要更新 {DisplayName}: 本地={LocalVersion}, 目标={RemoteVersion}", displayName, localVersion, version);
        setLoadingMessage?.Invoke("正在更新依赖库...");

        if (runtimeDirectory.Exists)
            runtimeDirectory.Delete(true);

        runtimeDirectory.Create();

        var downloadPath = PlatformHelpers.GetTempFileName();

        try
        {
            var packageBaseAddress = await GetPackageBaseAddressAsync(cancellationToken).ConfigureAwait(false);
            var dotnetUrl = $"{packageBaseAddress}/microsoft.netcore.app.runtime.{runtimeIdentifier}/{version}/microsoft.netcore.app.runtime.{runtimeIdentifier}.{version}.nupkg";
            var desktopUrl = $"{packageBaseAddress}/microsoft.windowsdesktop.app.runtime.{runtimeIdentifier}/{version}/microsoft.windowsdesktop.app.runtime.{runtimeIdentifier}.{version}.nupkg";
            var dotnetVersion = version.Split('.')[0];

            Log.Information("[Runtime] 正在下载 .NET 运行时 {RuntimeIdentifier} v{Version}", runtimeIdentifier, version);
            await DownloadFiles([(dotnetUrl, downloadPath)], reportProgress).ConfigureAwait(false);
            ExtractSpecificDirectory(downloadPath, Path.Combine(runtimeDirectory.FullName, "shared", "Microsoft.NETCore.App", version), $"runtimes/{runtimeIdentifier}/native/");
            ExtractSpecificDirectory(downloadPath, Path.Combine(runtimeDirectory.FullName, "shared", "Microsoft.NETCore.App", version), $"runtimes/{runtimeIdentifier}/lib/net{dotnetVersion}.0/");

            Log.Information("[Runtime] 正在下载 .NET 桌面运行时 {RuntimeIdentifier} v{Version}", runtimeIdentifier, version);
            await DownloadFiles([(desktopUrl, downloadPath)], reportProgress).ConfigureAwait(false);
            ExtractSpecificDirectory(downloadPath, Path.Combine(runtimeDirectory.FullName, "shared", "Microsoft.WindowsDesktop.App", version), $"runtimes/{runtimeIdentifier}/native/");
            ExtractSpecificDirectory
                (downloadPath, Path.Combine(runtimeDirectory.FullName, "shared", "Microsoft.WindowsDesktop.App", version), $"runtimes/{runtimeIdentifier}/lib/net{dotnetVersion}.0/");

            Directory.CreateDirectory(Path.Combine(runtimeDirectory.FullName, "host", "fxr", version));
            File.Move
            (
                Path.Combine(runtimeDirectory.FullName, "shared", "Microsoft.NETCore.App", version, "hostfxr.dll"),
                Path.Combine(runtimeDirectory.FullName, "host",   "fxr",                   version, "hostfxr.dll"),
                true
            );

            File.WriteAllText(versionFile.FullName, version);
            Log.Information("[Runtime] {DisplayName} 更新完成", displayName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Runtime] {DisplayName} 更新失败", displayName);
            throw;
        }
        finally
        {
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
        }
    }

    private static DirectoryInfo[] GetRequiredPaths(DirectoryInfo runtimeDirectory, string version) =>
    [
        new(Path.Combine(runtimeDirectory.FullName, "host",   "fxr",                          version)),
        new(Path.Combine(runtimeDirectory.FullName, "shared", "Microsoft.NETCore.App",        version)),
        new(Path.Combine(runtimeDirectory.FullName, "shared", "Microsoft.WindowsDesktop.App", version))
    ];

    private static string GetLocalRuntimeVersion(FileInfo versionFile)
    {
        try
        {
            if (versionFile.Exists)
                return File.ReadAllText(versionFile.FullName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Runtime] 无法读取本地运行时版本");
        }

        return string.Empty;
    }

    private static async Task DownloadFiles(IReadOnlyList<(string Url, string Path)> files, Action<long?, long, double?>? reportProgress)
    {
        using var downloader = new HttpClientDownloadWithProgress(files);
        if (reportProgress != null)
            downloader.ProgressChanged += (size, downloaded, progress) => reportProgress(size, downloaded, progress);

        await downloader.Download().ConfigureAwait(false);
    }

    private static void ExtractSpecificDirectory(string zipPath, string extractPath, string directoryToExtract)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(directoryToExtract, StringComparison.OrdinalIgnoreCase))
                continue;

            var destinationPath      = Path.Combine(extractPath, entry.FullName[directoryToExtract.Length..]);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrEmpty(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            if (!string.IsNullOrEmpty(entry.Name))
                entry.ExtractToFile(destinationPath, true);
        }
    }

    private static async Task<string> GetPackageBaseAddressAsync(CancellationToken cancellationToken)
    {
        using var testHttpClient = XLHttpClientFactory.Create(TimeSpan.FromSeconds(3), 8, DecompressionMethods.All);
        testHttpClient.Timeout = TimeSpan.FromSeconds(3);

        var googleTask = GetConnectionTimeAsync(testHttpClient, Links.GOOGLE_URL,       cancellationToken);
        var huaweiTask = GetConnectionTimeAsync(testHttpClient, Links.HUAWEI_CLOUD_URL, cancellationToken);

        await Task.WhenAll(googleTask, huaweiTask).ConfigureAwait(false);

        var googleResult = await googleTask.ConfigureAwait(false);
        var huaweiResult = await huaweiTask.ConfigureAwait(false);

        Log.Information("谷歌连接耗时: {GoogleTime:F2} ms, 状态: {GoogleStatus}",  googleResult.Elapsed.TotalMilliseconds, googleResult.IsSuccess ? "成功" : "失败");
        Log.Information("华为云连接耗时: {HuaweiTime:F2} ms, 状态: {HuaweiStatus}", huaweiResult.Elapsed.TotalMilliseconds, huaweiResult.IsSuccess ? "成功" : "失败");

        if (!googleResult.IsSuccess || huaweiResult.IsSuccess && huaweiResult.Elapsed < googleResult.Elapsed)
            return Links.HUAWEI_NUGET_V3_REMOTE_URL;

        return Links.NUGET_V3_FLAT_CONTAINER_URL;
    }

    private static async Task<(bool IsSuccess, TimeSpan Elapsed)> GetConnectionTimeAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        var stopwatch = new Stopwatch();

        try
        {
            stopwatch.Start();
            using var request  = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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

    #region Constants

    private const string WIN_X64_RUNTIME_IDENTIFIER = "win-x64";

    #endregion
}
