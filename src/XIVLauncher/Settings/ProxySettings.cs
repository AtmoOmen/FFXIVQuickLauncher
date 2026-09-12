using XIVLauncher.Common.Http;

namespace XIVLauncher.Settings;

/// <summary>
///     启动器网络代理配置 (独立存储于 Roaming 目录下的 proxyConfigV3.json)
/// </summary>
public sealed class ProxySettings
{
    /// <summary>
    ///     代理配置条目 (预设)
    /// </summary>
    public List<ProxyProfile> Profiles { get; set; } = [];

    /// <summary>
    ///     当前生效的条目 Id;null 表示不使用代理
    /// </summary>
    public string? ActiveProfileId { get; set; }

    public ProxyProfile? GetActiveProfile() =>
        Profiles.FirstOrDefault(profile => string.Equals(profile.Id, ActiveProfileId, StringComparison.Ordinal));

    public ProxyConfigSnapshot? ToSnapshot() =>
        GetActiveProfile()?.ToSnapshot();
}
