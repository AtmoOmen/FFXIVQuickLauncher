using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.Settings;

/// <summary>
///     代理配置独立存储: Roaming 目录下的 proxyConfigV3.json
/// </summary>
public static class ProxySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true
    };

    public static ProxySettings Load(string path)
    {
        MigrateLegacyLocation(path);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new ProxySettings();

        try
        {
            var json     = File.ReadAllText(path, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<ProxySettings>(json, JsonOptions) ?? new ProxySettings();

            MigrateLegacyFlatFormat(json, settings);

            foreach (var profile in settings.Profiles)
                if (!string.IsNullOrWhiteSpace(profile.ProxyPasswordEncrypted) && profile.GetPassword() == null)
                {
                    Log.Warning("[ProxySettingsStore] 代理密码解密失败, 已清空密码字段: {ProfileName}", profile.DisplayName);
                    profile.ProxyPasswordEncrypted = string.Empty;
                }

            return settings;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ProxySettingsStore] 读取代理配置失败: {Path}", path);
            IsolateBrokenFile(path);
            return new ProxySettings();
        }
    }

    public static void Save(string path, ProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("代理配置路径不能为空", nameof(path));

        var directoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        var tempPath   = path + ".tmp";
        var backupPath = path + ".bak";
        var json       = JsonSerializer.Serialize(settings, JsonOptions);

        try
        {
            File.WriteAllText(tempPath, json, Encoding.UTF8);

            if (File.Exists(path))
                File.Replace(tempPath, path, backupPath, true);
            else
                File.Move(tempPath, path);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    /// <summary>
    ///     旧版本把代理配置放在启动器安装目录, 现在统一迁移到 Roaming:
    ///     复制成功后尽力删除旧文件 (安装目录可能只读, 删除失败不阻塞)
    /// </summary>
    private static void MigrateLegacyLocation(string currentPath)
    {
        var legacyPath = Paths.GetLegacyProxyConfigPath();

        try
        {
            if (File.Exists(currentPath)
                || !File.Exists(legacyPath)
                || string.Equals(currentPath, legacyPath, StringComparison.OrdinalIgnoreCase))
                return;

            var directoryPath = Path.GetDirectoryName(currentPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
                Directory.CreateDirectory(directoryPath);

            File.Copy(legacyPath, currentPath, overwrite: false);
            Log.Information("[ProxySettingsStore] 已将旧版代理配置从安装目录迁移到: {Path}", currentPath);

            try
            {
                File.Delete(legacyPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[ProxySettingsStore] 删除安装目录下的旧版代理配置失败 (可忽略): {LegacyPath}", legacyPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ProxySettingsStore] 迁移旧版代理配置失败: {LegacyPath} -> {CurrentPath}", legacyPath, currentPath);
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
                Name                  = "旧代理配置",
                ProxyType             = root.GetProperty("ProxyType").GetInt32() is var type && Enum.IsDefined(typeof(ProxyType), type) ? (ProxyType)type : ProxyType.None,
                ProxyHost             = root.TryGetProperty("ProxyHost", out var host) ? host.GetString() ?? string.Empty : string.Empty,
                ProxyPort             = root.TryGetProperty("ProxyPort", out var port) ? port.GetInt32() : 0,
                ProxyUsername         = root.TryGetProperty("ProxyUsername", out var username) ? username.GetString() ?? string.Empty : string.Empty,
                ProxyPasswordEncrypted = root.TryGetProperty("ProxyPasswordEncrypted", out var password) ? password.GetString() ?? string.Empty : string.Empty
            };

            settings.Profiles.Add(profile);
            if (profile.ProxyType != ProxyType.None)
                settings.ActiveProfileId = profile.Id;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ProxySettingsStore] 旧版扁平代理配置迁移失败");
        }
    }

    private static void IsolateBrokenFile(string path)
    {
        try
        {
            var brokenPath = $"{path}.broken-{DateTime.Now:yyyyMMddHHmmssfff}";
            File.Move(path, brokenPath);
            Log.Warning("[ProxySettingsStore] 已隔离损坏的代理配置文件: {BrokenPath}", brokenPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ProxySettingsStore] 隔离损坏的代理配置文件失败: {Path}", path);
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // ignored
        }
    }
}
