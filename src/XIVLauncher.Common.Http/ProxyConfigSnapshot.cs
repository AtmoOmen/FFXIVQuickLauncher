namespace XIVLauncher.Common.Http;

/// <summary>
///     代理配置快照, 由应用设置层提供, 避免公共库依赖 UI 层类型
/// </summary>
public sealed record ProxyConfigSnapshot
(
    string  Type,
    string  Host,
    int     Port,
    string? Username = null,
    string? Password = null
)
{
    public bool IsDisabled =>
        string.IsNullOrWhiteSpace(Host)
        || Port is < 1 or > 65535
        || string.IsNullOrWhiteSpace(Type)
        || string.Equals(Type, "None", StringComparison.OrdinalIgnoreCase);
}
