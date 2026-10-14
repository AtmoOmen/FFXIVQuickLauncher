using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;
using XIVLauncher.GamePatchV3.Integrity.Models;
using XIVLauncher.GamePatchV3.Models;
using XIVLauncher.GamePatchV3.Update.Models;

namespace XIVLauncher.GamePatchV3;

public sealed class GamePatchMetadataClient : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient client = new
    (
        new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy    = XLProxyProvider.Current
        }
    );

    public void Dispose() =>
        client.Dispose();

    public async Task<GameUpdatePlan?> BuildUpdatePlan
    (
        string            currentGameVersion,
        bool              forceUpdate,
        CancellationToken cancellationToken = default
    )
    {
        var remoteVersion = await DownloadRemoteVersion(cancellationToken).ConfigureAwait(false);
        return BuildPlanFromRemoteVersion(remoteVersion, currentGameVersion, forceUpdate);
    }

    internal static GameUpdatePlan? BuildPlanFromRemoteVersion
    (
        RemoteVersion remoteVersion,
        string        currentGameVersion,
        bool          forceUpdate
    )
    {
        var normalizedGameVersion = NormalizeGameVersion(currentGameVersion);
        Log.Information("[V3Patch] 正在构建更新计划, 当前游戏版本 {CurrentGameVersion}, 强制更新 {ForceUpdate}", normalizedGameVersion, forceUpdate);
        var targetArea                  = GetTargetArea(remoteVersion);
        var minimumSupportedDataVersion = ResolveMinimumSupportedDataVersion(targetArea);
        var resolved                    = ResolveLocalVersion(normalizedGameVersion, remoteVersion);
        var currentVersion              = resolved.DataVersion;
        var currentViewVersion          = resolved.ViewVersion;

        if (string.IsNullOrWhiteSpace(currentVersion))
            throw new UnsupportedGameVersionException(CreateUnsupportedVersionMessage(normalizedGameVersion, minimumSupportedDataVersion));

        if (!IsSupportedDataVersion(currentVersion, minimumSupportedDataVersion))
            throw new UnsupportedGameVersionException(CreateUnsupportedVersionMessage(normalizedGameVersion, minimumSupportedDataVersion, currentVersion));

        if (!forceUpdate && string.Equals(currentVersion, targetArea.Must, StringComparison.Ordinal))
        {
            Log.Information("[V3Patch] 当前版本已是目标版本, 数据版本 {CurrentVersion}", currentVersion);
            return null;
        }

        var targetGameVersion = ResolveGameVersion(remoteVersion, targetArea.Must);

        var packages = FindUpdatePackages(remoteVersion.Packages, currentVersion, targetArea.Must);

        foreach (var package in packages)
            Log.Information("[V3Patch] 已选择更新包 {PackageName}, 版本 {FromVersion} -> {ToVersion}", package.Name, package.From, package.To);

        var updatePlan = new GameUpdatePlan
        {
            BaseUrl            = remoteVersion.BaseUrl,
            BackupBaseUrl      = remoteVersion.BackupBaseUrl,
            CurrentGameVersion = normalizedGameVersion,
            CurrentDataVersion = currentVersion,
            CurrentViewVersion = currentViewVersion,
            TargetGameVersion  = targetGameVersion,
            TargetDataVersion  = targetArea.Must,
            TargetViewVersion  = ResolveTargetViewVersion(remoteVersion, targetArea),
            Packages           = packages
        };
        Log.Information
        (
            "[V3Patch] 更新计划构建完成, 当前数据版本 {CurrentDataVersion}, 目标数据版本 {TargetDataVersion}, 包数量 {PackageCount}",
            updatePlan.CurrentDataVersion,
            updatePlan.TargetDataVersion,
            updatePlan.Packages.Count
        );
        return updatePlan;
    }

    private static List<GameVersionPackage> FindUpdatePackages
    (
        IReadOnlyList<GameVersionPackage> availablePackages,
        string                            currentVersion,
        string                            targetVersion
    )
    {
        if (string.Equals(currentVersion, targetVersion, StringComparison.Ordinal))
            return [];

        var versionsToVisit = new Queue<string>();
        var predecessors    = new Dictionary<string, (string PreviousVersion, GameVersionPackage Package)>(StringComparer.Ordinal);
        var visited         = new HashSet<string>(StringComparer.Ordinal) { currentVersion };
        versionsToVisit.Enqueue(currentVersion);

        while (versionsToVisit.TryDequeue(out var version))
        {
            foreach (var package in availablePackages)
            {
                if (!string.Equals(package.From, version, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(package.To) || !visited.Add(package.To))
                    continue;

                predecessors.Add(package.To, (version, package));
                if (string.Equals(package.To, targetVersion, StringComparison.Ordinal))
                    return BuildPackagePath(predecessors, currentVersion, targetVersion);

                versionsToVisit.Enqueue(package.To);
            }
        }

        throw new InvalidDataException($"未找到 V3 更新包: {currentVersion} -> {targetVersion}");
    }

    private static List<GameVersionPackage> BuildPackagePath
    (
        IReadOnlyDictionary<string, (string PreviousVersion, GameVersionPackage Package)> predecessors,
        string                                                                            currentVersion,
        string                                                                            targetVersion
    )
    {
        List<GameVersionPackage> packages = [];
        var                      cursor   = targetVersion;

        while (!string.Equals(cursor, currentVersion, StringComparison.Ordinal))
        {
            var predecessor = predecessors[cursor];
            packages.Add(predecessor.Package);
            cursor = predecessor.PreviousVersion;
        }

        packages.Reverse();
        return packages;
    }

    public async Task<IntegrityCheckResult> DownloadIntegrityCheck
    (
        CancellationToken cancellationToken = default
    )
    {
        var remoteVersion = await DownloadRemoteVersion(cancellationToken).ConfigureAwait(false);
        return await DownloadIntegrityCheck(remoteVersion, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IntegrityCheckResult> DownloadIntegrityCheck
    (
        RemoteVersion     remoteVersion,
        CancellationToken cancellationToken = default
    )
    {
        var responseText   = await DownloadString(SdoInfos.CLIENT_ALL_FILES_LIST_URL, cancellationToken).ConfigureAwait(false);
        var integrityLines = responseText.Trim().Split();
        if (integrityLines.Length == 0)
            throw new InvalidDataException("未能解析 client_all_files_list.dat");

        var headerParts = integrityLines[0].Split('|');
        if (headerParts.Length < 3)
            throw new InvalidDataException("client_all_files_list.dat 头部格式无效");

        var gameVersion = ResolveGameVersion(remoteVersion, headerParts[2]);
        Log.Information("[V3Patch] 完整性清单目标数据版本 {DataVersion}, 目标游戏版本 {GameVersion}", headerParts[2], gameVersion);

        var result = new IntegrityCheckResult
        {
            AppId       = headerParts[1],
            BaseUrl     = headerParts[0],
            DataVersion = headerParts[2],
            GameVersion = gameVersion
        };

        for (var i = 1; i < integrityLines.Length; i++)
        {
            var lineParts = integrityLines[i].Split('|');
            if (lineParts.Length < 3)
                continue;

            if (!GamePathNormalizer.TryNormalizeGameRelativePath(lineParts[0], out _) || string.IsNullOrWhiteSpace(lineParts[2]))
                continue;

            var filePath = GamePathNormalizer.NormalizeDownloadPath(lineParts[0]);
            result.Hashes[filePath] = lineParts[2];

            if (!ulong.TryParse(lineParts[1], out var fileSize))
            {
                result.Hashes.Remove(filePath);
                continue;
            }

            result.Sizes[filePath] = fileSize;
        }

        return result;
    }

    public async Task<RemoteVersion> DownloadRemoteVersion
    (
        CancellationToken cancellationToken = default
    )
    {
        var json = await DownloadString(SdoInfos.REMOTE_VERSION_URL, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<RemoteVersion>(json, SerializerOptions) ?? throw new InvalidDataException("未能解析 V3 远端版本信息");
    }

    private static GameVersionArea GetTargetArea
    (
        RemoteVersion remoteVersion
    )
    {
        var targetArea = remoteVersion.Areas.FirstOrDefault(area => area.Id == "0") ?? remoteVersion.Areas.FirstOrDefault();
        return targetArea ?? throw new InvalidDataException("V3 远端版本信息缺少 area 配置");
    }

    private static string ResolveTargetViewVersion
    (
        RemoteVersion   remoteVersion,
        GameVersionArea targetArea
    )
    {
        var packageView = ResolveGameVersion(remoteVersion, targetArea.Must);

        if (!string.IsNullOrWhiteSpace(packageView))
            return packageView;

        if (!string.IsNullOrWhiteSpace(targetArea.View))
            return NormalizeVersionView(targetArea.View);

        return string.Empty;
    }

    private async Task<string> DownloadString
    (
        string            url,
        CancellationToken cancellationToken
    )
    {
        var ticks = Stopwatch.GetTimestamp();
        Log.Information("[V3Patch] 正在下载元数据 {Url}", url);
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Log.Information("[V3Patch] 元数据下载完成 {Url}, 字符数 {Length}, 耗时 {ElapsedMs} ms", url, content.Length, Stopwatch.GetElapsedTime(ticks).TotalMilliseconds);
        return content;
    }

    internal static (string DataVersion, string ViewVersion) ResolveLocalVersion
    (
        string        currentGameVersion,
        RemoteVersion remoteVersion
    )
    {
        var normalizedGameVersion = NormalizeGameVersion(currentGameVersion);
        if (string.IsNullOrWhiteSpace(normalizedGameVersion))
            return (string.Empty, string.Empty);

        var matchedPackage = remoteVersion.Packages.FirstOrDefault(package => IsMatchingGameVersion(package.VersionView, normalizedGameVersion));
        if (matchedPackage != null)
            return (matchedPackage.To, NormalizeVersionView(matchedPackage.VersionView));

        return (string.Empty, normalizedGameVersion);
    }

    internal static string ResolveGameVersion
    (
        RemoteVersion remoteVersion,
        string        dataVersion
    )
    {
        var matchedPackage = remoteVersion.Packages.FirstOrDefault(package => string.Equals(package.To, dataVersion, StringComparison.Ordinal));
        if (matchedPackage != null)
            return NormalizeVersionView(matchedPackage.VersionView);

        var matchedArea = remoteVersion.Areas.FirstOrDefault
        (area => string.Equals(area.Must, dataVersion, StringComparison.Ordinal) || string.Equals(area.Max, dataVersion, StringComparison.Ordinal)
        );
        return matchedArea == null ?
                   string.Empty :
                   NormalizeVersionView(matchedArea.View);
    }

    internal static bool IsSupportedDataVersion
    (
        string dataVersion,
        string minimumSupportedDataVersion
    ) =>
        !string.IsNullOrWhiteSpace(dataVersion) && CompareDataVersions(dataVersion, minimumSupportedDataVersion) >= 0;

    internal static int CompareDataVersions
    (
        string left,
        string right
    )
    {
        var leftParts  = left.Split('.');
        var rightParts = right.Split('.');
        var partCount  = Math.Max(leftParts.Length, rightParts.Length);

        for (var index = 0; index < partCount; index++)
        {
            var leftValue = index < leftParts.Length && int.TryParse(leftParts[index], out var parsedLeft) ?
                                parsedLeft :
                                0;
            var rightValue = index < rightParts.Length && int.TryParse(rightParts[index], out var parsedRight) ?
                                 parsedRight :
                                 0;
            var compare = leftValue.CompareTo(rightValue);
            if (compare != 0)
                return compare;
        }

        return 0;
    }

    internal static string ResolveMinimumSupportedDataVersion
    (
        GameVersionArea targetArea
    )
    {
        var normalizedMinimum = NormalizeGameVersion(targetArea.Min);
        return string.IsNullOrWhiteSpace(normalizedMinimum) ?
                   SdoInfos.DEFAULT_MINIMUM_SUPPORTED_DATA_VERSION :
                   normalizedMinimum;
    }

    private static string CreateUnsupportedVersionMessage
    (
        string  gameVersion,
        string  minimumSupportedDataVersion,
        string? dataVersion = null
    )
    {
        var versionText = string.IsNullOrWhiteSpace(dataVersion) ?
                              $"游戏版本 {gameVersion}" :
                              $"游戏版本 {gameVersion}, 数据版本 {dataVersion}";
        return $"当前游戏版本过旧或无法识别, {versionText}, 最低支持的数据版本为 {minimumSupportedDataVersion}, 请先使用“修复游戏文件”更新到最新版本, 或重新下载完整游戏";
    }

    private static bool IsMatchingGameVersion
    (
        string versionView,
        string gameVersion
    )
    {
        if (string.IsNullOrWhiteSpace(versionView))
            return false;

        var normalizedVersionView = NormalizeVersionView(versionView);
        return string.Equals
                   (normalizedVersionView, gameVersion, StringComparison.Ordinal) ||
               string.Equals(NormalizeGameVersion(versionView), gameVersion, StringComparison.Ordinal);
    }

    private static string NormalizeGameVersion
    (
        string gameVersion
    ) =>
        gameVersion.Trim().Trim('\uFEFF').Trim();

    private static string NormalizeVersionView
    (
        string? versionView
    )
    {
        if (string.IsNullOrWhiteSpace(versionView))
            return string.Empty;

        var normalizedVersionView = NormalizeGameVersion(versionView);
        var separatorIndex        = normalizedVersionView.IndexOf('_');
        return separatorIndex < 0 ?
                   normalizedVersionView :
                   normalizedVersionView[..separatorIndex];
    }
}
