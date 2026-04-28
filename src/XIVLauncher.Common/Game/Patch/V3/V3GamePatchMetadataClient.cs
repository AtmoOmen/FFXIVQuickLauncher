using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game.Integrity;

namespace XIVLauncher.Common.Game.Patch.V3;

public sealed class V3GamePatchMetadataClient : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient client = new();

    public void Dispose() =>
        client.Dispose();

    public async Task<V3GameUpdatePlan?> BuildUpdatePlan(string currentGameVersion, bool forceUpdate, CancellationToken cancellationToken = default)
    {
        Log.Information("[V3Patch] 正在构建更新计划, 当前游戏版本 {CurrentGameVersion}, 强制更新 {ForceUpdate}", currentGameVersion, forceUpdate);
        var remoteVersion  = await DownloadRemoteVersion(cancellationToken).ConfigureAwait(false);
        var versionMapping = await DownloadVersionMapping(cancellationToken).ConfigureAwait(false);
        var targetArea     = GetTargetArea(remoteVersion);
        var currentMapping = versionMapping.GetValueOrDefault(currentGameVersion);
        var currentVersion = currentMapping?.V ?? string.Empty;

        if (!forceUpdate && string.Equals(currentVersion, targetArea.Must, StringComparison.Ordinal))
        {
            Log.Information("[V3Patch] 当前版本已是目标版本, 数据版本 {CurrentVersion}", currentVersion);
            return null;
        }

        var targetGameVersion = versionMapping
                                .FirstOrDefault(entry => string.Equals(entry.Value.V, targetArea.Must, StringComparison.Ordinal))
                                .Key
                                ?? string.Empty;

        List<V3GameVersionPackage> packages = [];
        var                        cursor   = currentVersion;
        HashSet<string>            visited  = [];

        while (!string.Equals(cursor, targetArea.Must, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(cursor) || !visited.Add(cursor))
                throw new InvalidDataException("未能解析可用的 V3 更新路径");

            var package = remoteVersion.Packages.FirstOrDefault(entry => string.Equals(entry.From, cursor,          StringComparison.Ordinal)
                                                                      && string.Equals(entry.To,   targetArea.Must, StringComparison.Ordinal))
                          ?? remoteVersion.Packages.FirstOrDefault(entry => string.Equals(entry.From, cursor, StringComparison.Ordinal));

            if (package == null)
                throw new InvalidDataException($"未找到 V3 更新包: {cursor} -> {targetArea.Must}");

            packages.Add(package);
            Log.Information("[V3Patch] 已选择更新包 {PackageName}, 版本 {FromVersion} -> {ToVersion}", package.Name, package.From, package.To);
            cursor = package.To;
        }

        var updatePlan = new V3GameUpdatePlan
        {
            BaseUrl            = remoteVersion.BaseUrl,
            BackupBaseUrl      = remoteVersion.BackupBaseUrl,
            CurrentGameVersion = currentGameVersion,
            CurrentDataVersion = currentVersion,
            CurrentViewVersion = currentMapping?.View ?? currentGameVersion,
            TargetGameVersion  = targetGameVersion,
            TargetDataVersion  = targetArea.Must,
            TargetViewVersion  = ResolveTargetViewVersion(remoteVersion, targetArea, versionMapping),
            Packages           = packages
        };
        Log.Information("[V3Patch] 更新计划构建完成, 当前数据版本 {CurrentDataVersion}, 目标数据版本 {TargetDataVersion}, 包数量 {PackageCount}", updatePlan.CurrentDataVersion, updatePlan.TargetDataVersion, updatePlan.Packages.Count);
        return updatePlan;
    }

    public async Task<IntegrityCheckResult> DownloadIntegrityCheck(CancellationToken cancellationToken = default)
    {
        var responseText   = await DownloadString(SdoInfos.CLIENT_ALL_FILES_LIST_URL, cancellationToken).ConfigureAwait(false);
        var integrityLines = responseText.Trim().Split();
        if (integrityLines.Length == 0)
            throw new InvalidDataException("未能解析 client_all_files_list.dat");

        var headerParts = integrityLines[0].Split('|');
        if (headerParts.Length < 3)
            throw new InvalidDataException("client_all_files_list.dat 头部格式无效");

        var versionMapping = await DownloadVersionMapping(cancellationToken).ConfigureAwait(false);
        var gameVersion = versionMapping
                          .FirstOrDefault(entry => string.Equals(entry.Value.V, headerParts[2], StringComparison.Ordinal))
                          .Key
                          ?? string.Empty;

        var result = new IntegrityCheckResult
        {
            AppId                 = headerParts[1],
            BaseUrl               = headerParts[0],
            DataVersion           = headerParts[2],
            GameVersion           = gameVersion,
            LatestLocalVersionUrl = SdoInfos.LATEST_LOCAL_VERSION_FILE_URL
        };

        for (var i = 1; i < integrityLines.Length; i++)
        {
            var lineParts = integrityLines[i].Split('|');
            if (lineParts.Length < 3)
                continue;

            var filePath = lineParts[0];
            if (!filePath.StartsWith('\\'))
                filePath = "\\" + filePath;
            if (!filePath.StartsWith(@"\game\", StringComparison.Ordinal))
                continue;

            result.Hashes[filePath] = lineParts[2];
            result.Sizes[filePath]  = ulong.Parse(lineParts[1]);
        }

        return result;
    }

    public async Task<Dictionary<string, V3VersionMappingEntry>> DownloadVersionMapping(CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var url       = $"{SdoInfos.VERSION_MAPPING_URL}?time={timestamp}";
        var json      = await DownloadString(url, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Dictionary<string, V3VersionMappingEntry>>(json, SerializerOptions)
               ?? throw new InvalidDataException("未能解析 V3 版本映射");
    }

    public async Task<V3RemoteVersion> DownloadRemoteVersion(CancellationToken cancellationToken = default)
    {
        var json = await DownloadString(SdoInfos.REMOTE_VERSION_URL, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<V3RemoteVersion>(json, SerializerOptions)
               ?? throw new InvalidDataException("未能解析 V3 远端版本信息");
    }

    public Task<string> DownloadLatestLocalVersionFile(CancellationToken cancellationToken = default) =>
        DownloadString(SdoInfos.LATEST_LOCAL_VERSION_FILE_URL, cancellationToken);

    private static V3GameVersionArea GetTargetArea(V3RemoteVersion remoteVersion)
    {
        var targetArea = remoteVersion.Areas.FirstOrDefault(area => area.Id == "0") ?? remoteVersion.Areas.FirstOrDefault();
        return targetArea ?? throw new InvalidDataException("V3 远端版本信息缺少 area 配置");
    }

    private static string ResolveTargetViewVersion
    (
        V3RemoteVersion                                    remoteVersion,
        V3GameVersionArea                                  targetArea,
        IReadOnlyDictionary<string, V3VersionMappingEntry> versionMapping
    )
    {
        var packageView = remoteVersion.Packages
                                       .FirstOrDefault(package => string.Equals(package.To, targetArea.Must, StringComparison.Ordinal))
                                       ?.VersionView;

        if (!string.IsNullOrWhiteSpace(packageView))
            return packageView;

        if (!string.IsNullOrWhiteSpace(targetArea.View))
            return targetArea.View;

        return versionMapping
               .FirstOrDefault(entry => string.Equals(entry.Value.V, targetArea.Must, StringComparison.Ordinal))
               .Value
               ?.View
               ?? string.Empty;
    }

    private async Task<string> DownloadString(string url, CancellationToken cancellationToken)
    {
        var ticks = Stopwatch.GetTimestamp();
        Log.Information("[V3Patch] 正在下载元数据 {Url}", url);
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Log.Information("[V3Patch] 元数据下载完成 {Url}, 字符数 {Length}, 耗时 {ElapsedMs} ms", url, content.Length, Stopwatch.GetElapsedTime(ticks).TotalMilliseconds);
        return content;
    }
}
