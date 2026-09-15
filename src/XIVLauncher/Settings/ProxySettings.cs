using System.Text.Json;
using XIVLauncher.Common.Http;

namespace XIVLauncher.Settings;

/// <summary>
///     启动器网络代理配置 (随启动器主配置 LauncherSettingsV3 一并存储)
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

    /// <summary>
    ///     深拷贝一份配置副本, 供代理设置窗口的编辑会话使用
    ///     (取消编辑时不影响已生效的配置)
    /// </summary>
    public ProxySettings DeepClone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<ProxySettings>(json) ?? new ProxySettings();
    }
}
