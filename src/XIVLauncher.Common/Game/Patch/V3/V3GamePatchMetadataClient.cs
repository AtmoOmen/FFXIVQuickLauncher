using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
        var remoteVersion  = await DownloadRemoteVersion(cancellationToken).ConfigureAwait(false);
        var versionMapping = await DownloadVersionMapping(cancellationToken).ConfigureAwait(false);
        var targetArea     = GetTargetArea(remoteVersion);
        var currentMapping = versionMapping.GetValueOrDefault(currentGameVersion);
        var currentVersion = currentMapping?.V ?? string.Empty;

        if (!forceUpdate && string.Equals(currentVersion, targetArea.Must, StringComparison.Ordinal))
            return null;

        var targetGameVersion = versionMapping
                                .FirstOrDefault(entry => string.Equals(entry.Value.V, targetArea.Must, StringComparison.Ordinal))
                                .Key
                                ?? string.Empty;

        return new()
        {
            CurrentGameVersion = currentGameVersion,
            CurrentDataVersion = currentVersion,
            CurrentViewVersion = currentMapping?.View ?? currentGameVersion,
            TargetGameVersion  = targetGameVersion,
            TargetDataVersion  = targetArea.Must,
            TargetViewVersion  = ResolveTargetViewVersion(remoteVersion, targetArea, versionMapping)
        };
    }

    public async Task<IntegrityCheckResult> DownloadIntegrityCheck(CancellationToken cancellationToken = default)
    {
        var responseText   = await DownloadString(V3GamePatchConstants.CLIENT_ALL_FILES_LIST_URL, cancellationToken).ConfigureAwait(false);
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
            LatestLocalVersionUrl = V3GamePatchConstants.LATEST_LOCAL_VERSION_FILE_URL
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
        var url       = $"{V3GamePatchConstants.VERSION_MAPPING_URL}?time={timestamp}";
        var json      = await DownloadString(url, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Dictionary<string, V3VersionMappingEntry>>(json, SerializerOptions)
               ?? throw new InvalidDataException("未能解析 V3 版本映射");
    }

    public async Task<V3RemoteVersion> DownloadRemoteVersion(CancellationToken cancellationToken = default)
    {
        var json = await DownloadString(V3GamePatchConstants.REMOTE_VERSION_URL, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<V3RemoteVersion>(json, SerializerOptions)
               ?? throw new InvalidDataException("未能解析 V3 远端版本信息");
    }

    public Task<string> DownloadLatestLocalVersionFile(CancellationToken cancellationToken = default) =>
        DownloadString(V3GamePatchConstants.LATEST_LOCAL_VERSION_FILE_URL, cancellationToken);

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
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
