using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.Settings;

/// <summary>
///     旧版独立代理配置 (proxyConfigV3.json) 迁移:
///     代理配置现已并入启动器主配置 (LauncherSettingsV3) 的统一存储体系,
///     启动时自动导入旧文件, 导入成功后删除旧文件。
/// </summary>
public static class ProxySettingsMigration
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    ///     若旧版代理配置文件存在且主配置中尚无代理配置, 则导入主配置并删除旧文件
    /// </summary>
    public static void ImportLegacyInto(LauncherSettingsV3 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var sourcePath = ResolveLegacySourcePath();
            if (sourcePath == null)
                return;

            var legacy = TryLoadLegacyFile(sourcePath);
            if (legacy == null)
            {
                IsolateBrokenFile(sourcePath);
                return;
            }

            if (legacy.Profiles.Count == 0)
                return;

            if (settings.ProxySettings.Profiles.Count > 0 || !string.IsNullOrWhiteSpace(settings.ProxySettings.ActiveProfileId))
            {
                Log.Information("[ProxySettingsMigration] 主配置中已存在代理配置, 跳过旧文件导入: {Path}", sourcePath);
                return;
            }

            settings.ProxySettings = legacy;
            settings.Save();

            Log.Information("[ProxySettingsMigration] 旧版代理配置已并入主配置: {Path}", sourcePath);
            DeleteLegacyFile(sourcePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ProxySettingsMigration] 迁移旧版代理配置失败");
        }
    }

    /// <summary>
    ///     优先使用 Roaming 目录下的旧配置, 不存在时回退到启动器安装目录
    /// </summary>
    private static string? ResolveLegacySourcePath()
    {
        var roamingPath = Paths.GetProxyConfigPath();
        if (File.Exists(roamingPath))
            return roamingPath;

        var legacyPath = Paths.GetLegacyProxyConfigPath();
        return File.Exists(legacyPath) ? legacyPath : null;
    }

    private static ProxySettings? TryLoadLegacyFile(string path)
    {
        try
        {
            var json     = File.ReadAllText(path, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<ProxySettings>(json, JsonOptions) ?? new ProxySettings();

            MigrateLegacyFlatFormat(json, settings);

            foreach (var profile in settings.Profiles)
                if (!string.IsNullOrWhiteSpace(profile.ProxyPasswordEncrypted) && profile.GetPassword() == null)
                {
                    Log.Warning("[ProxySettingsMigration] 代理密码解密失败, 已清空密码字段: {ProfileName}", profile.DisplayName);
                    profile.ProxyPasswordEncrypted = string.Empty;
                }

            return settings;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ProxySettingsMigration] 读取旧版代理配置失败: {Path}", path);
            return null;
        }
    }

    /// <summary>
    ///     兼容早期单条扁平格式: 根节点直接存放代理字段时转换为单个条目
    /// </summary>
    private static void MigrateLegacyFlatFormat(string json, ProxySettings settings)
    {
        if (settings.Profiles.Count > 0)
            return;

        try
        {
            using var document = JsonDocument.Parse(json);
            var       root     = document.RootElement;
            if (!root.TryGetProperty("ProxyType", out _) || !root.TryGetProperty("ProxyHost", out _))
                return;

            var profile = new ProxyProfile
            {
                Name                   = "旧代理配置",
                ProxyType              = ParseLegacyProxyType(root),
                ProxyHost              = root.TryGetProperty("ProxyHost", out var host) ? host.GetString() ?? string.Empty : string.Empty,
                ProxyPort              = root.TryGetProperty("ProxyPort", out var port) ? port.GetInt32() : 0,
                ProxyUsername          = root.TryGetProperty("ProxyUsername", out var username) ? username.GetString() ?? string.Empty : string.Empty,
                ProxyPasswordEncrypted = root.TryGetProperty("ProxyPasswordEncrypted", out var password) ? password.GetString() ?? string.Empty : string.Empty
            };

            settings.Profiles.Add(profile);
            if (profile.ProxyType != ProxyType.None)
                settings.ActiveProfileId = profile.Id;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ProxySettingsMigration] 旧版扁平代理配置迁移失败");
        }
    }

    private static ProxyType ParseLegacyProxyType(JsonElement root)
    {
        var rawType = root.GetProperty("ProxyType").GetInt32();
        return Enum.IsDefined(typeof(ProxyType), rawType) ? (ProxyType)rawType : ProxyType.None;
    }

    private static void IsolateBrokenFile(string path)
    {
        try
        {
            var brokenPath = $"{path}.broken-{DateTime.Now:yyyyMMddHHmmssfff}";
            File.Move(path, brokenPath);
            Log.Warning("[ProxySettingsMigration] 已隔离损坏的旧版代理配置文件: {BrokenPath}", brokenPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ProxySettingsMigration] 隔离损坏的旧版代理配置文件失败: {Path}", path);
        }
    }

    /// <summary>
    ///     导入成功后尽力删除导入源文件 (删除失败不阻塞, 残留文件仅作历史遗留)
    /// </summary>
    private static void DeleteLegacyFile(string sourcePath)
    {
        try
        {
            if (File.Exists(sourcePath))
                File.Delete(sourcePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ProxySettingsMigration] 删除旧版代理配置文件失败 (可忽略): {LegacyPath}", sourcePath);
        }
    }
}
