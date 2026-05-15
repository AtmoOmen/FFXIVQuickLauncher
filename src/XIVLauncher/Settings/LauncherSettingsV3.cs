using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Account.Cred;
using XIVLauncher.Common;
using XIVLauncher.CompanionApp;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Dalamud;
using XIVLauncher.Settings.Converters;
using XIVLauncher.Xaml;

namespace XIVLauncher.Settings;

/// <summary>
///     启动器配置 V3 版本
/// </summary>
public sealed class LauncherSettingsV3 : IAccountSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions    = CreateJsonOptions();
    private static readonly UTF8Encoding          Utf8WithoutBom = new(false);

    private string? configPath;
    private bool    suppressSave = true;
    private int     batchUpdateDepth;
    private bool    hasPendingSave;

    #region 游戏路径配置

    /// <summary>
    ///     游戏安装路径
    /// </summary>
    public DirectoryInfo GamePath
    {
        get;
        set => Set(ref field, value);
    } = null!;

    /// <summary>
    ///     补丁文件存储路径
    /// </summary>
    public DirectoryInfo PatchPath
    {
        get;
        set => Set(ref field, value);
    } = null!;

    #endregion

    #region 游戏启动配置

    /// <summary>
    ///     附加启动参数
    /// </summary>
    public string AdditionalLaunchArgs
    {
        get;
        set => Set(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     选中的登录方式
    /// </summary>
    public LoginType SelectedLoginType
    {
        get;
        set => Set(ref field, value);
    } = LoginType.Slide;

    /// <summary>
    ///     选中的服务器索引
    /// </summary>
    public int SelectedServer
    {
        get;
        set => Set(ref field, value);
    } = 0;

    /// <summary>
    ///     是否启用快速登录
    /// </summary>
    public bool FastLogin
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    ///     是否加密启动参数 V2
    /// </summary>
    public bool EncryptArgumentsV2
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    ///     DPI 感知模式
    /// </summary>
    public DPIAwareness DPIAwareness
    {
        get;
        set => Set(ref field, value);
    } = DPIAwareness.Aware;

    /// <summary>
    ///     是否将非零退出码视为失败
    /// </summary>
    public bool TreatNonZeroExitCodeAsFailure
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>
    ///     游戏退出时是否关闭启动器
    /// </summary>
    public bool ExitLauncherWhenGameExit
    {
        get;
        set => Set(ref field, value);
    }

    #endregion

    #region Dalamud 插件配置

    /// <summary>
    ///     是否启用 Dalamud 插件系统
    /// </summary>
    public bool DalamudEnabled
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    ///     Dalamud 加载方式
    /// </summary>
    public DalamudLoadMethod DalamudLoadMethod
    {
        get;
        set => Set(ref field, value);
    } = DalamudLoadMethod.EntryPoint;

    /// <summary>
    ///     Dalamud 注入延迟（毫秒）
    /// </summary>
    public decimal DalamudInjectionDelayMS
    {
        get;
        set => Set(ref field, value);
    } = 0;

    /// <summary>
    ///     是否启用手动注入自动注入
    /// </summary>
    public bool ManualInjectAutoInjectEnabled
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>
    ///     手动注入延迟（毫秒）
    /// </summary>
    public decimal ManualInjectDelayMs
    {
        get;
        set => Set(ref field, value);
    } = 0;

    #endregion

    #region 插件/扩展配置

    /// <summary>
    ///     启动时附加的程序列表
    /// </summary>
    public List<CompanionAppEntry> CompanionAppList
    {
        get;
        set
        {
            value = value.Where(x => !string.IsNullOrEmpty(x.CompanionApp.FilePath)).ToList();
            Set(ref field, value);
        }
    } = [];

    public List<CompanionAppEntry>? AddonList
    {
        get => null;
        set
        {
            if (value == null || value.Count == 0)
                return;

            CompanionAppList = value;
        }
    }

    /// <summary>
    ///     是否已提示过 GShade DXGI 问题
    /// </summary>
    public bool HasComplainedAboutGShadeDXGI
    {
        get;
        set => Set(ref field, value);
    }

    #endregion

    #region 补丁更新配置

    /// <summary>
    ///     安装补丁前是否询问
    /// </summary>
    public bool AskBeforePatchInstall
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    ///     下载速度限制（字节/秒），0 表示无限制
    /// </summary>
    public long SpeedLimitBytes
    {
        get;
        set => Set(ref field, value);
    } = 0;

    /// <summary>
    ///     是否保留补丁文件
    /// </summary>
    public bool KeepPatches
    {
        get;
        set => Set(ref field, value);
    }

    #endregion

    #region 账户与安全配置

    /// <summary>
    ///     当前账户 ID
    /// </summary>
    public string CurrentAccountID
    {
        get;
        set => Set(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     新登录是否需要设备档案设置
    /// </summary>
    public bool RequireDeviceProfileSetupForNewLogin
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>
    ///     凭据存储类型
    /// </summary>
    public CredType CredType
    {
        get;
        set => Set(ref field, value);
    } = CredType.WindowsCredManager;

    #endregion

    #region 启动器界面配置

    /// <summary>
    ///     主窗口位置和状态
    /// </summary>
    public PreserveWindowPosition.WindowPlacement? MainWindowPlacement
    {
        get;
        set => Set(ref field, value);
    } = null;

    #endregion

    #region 高级/开发配置

    /// <summary>
    ///     版本升级级别
    /// </summary>
    public int VersionUpgradeLevel
    {
        get;
        set => Set(ref field, value);
    } = 0;

    /// <summary>
    ///     是否跳过更新检查
    /// </summary>
    public bool EnableSkipUpdate
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>
    ///     是否启用详细日志
    /// </summary>
    public bool EnableVerboseLog
    {
        get;
        set => Set(ref field, value);
    }

    #endregion

    /// <summary>
    ///     从指定路径加载配置
    /// </summary>
    public static LauncherSettingsV3 Load(string configPath)
    {
        if (TryLoadSettings(configPath, configPath, out var settings, out var configException))
            return settings;

        if (configException != null)
        {
            Log.Error(configException, "读取启动器配置失败: {ConfigPath}", configPath);

            var brokenPath = MoveBrokenConfig(configPath);
            if (!string.IsNullOrWhiteSpace(brokenPath))
                Log.Warning("已隔离损坏配置文件: {BrokenPath}", brokenPath);
        }

        var backupPath = GetBackupPath(configPath);

        if (TryLoadSettings(backupPath, configPath, out settings, out var backupException))
        {
            Log.Warning("已从备份恢复启动器配置: {BackupPath}", backupPath);
            settings.Save();
            return settings;
        }

        if (backupException != null)
            Log.Error(backupException, "读取启动器配置备份失败: {BackupPath}", backupPath);

        return CreateDetachedSettings(configPath);
    }

    public void Update(Action<LauncherSettingsV3> updater)
    {
        ArgumentNullException.ThrowIfNull(updater);

        batchUpdateDepth++;
        var isCompleted = false;

        try
        {
            updater(this);
            isCompleted = true;
        }
        finally
        {
            batchUpdateDepth--;

            if (batchUpdateDepth == 0 && !isCompleted)
                hasPendingSave = false;
            else if (batchUpdateDepth == 0 && hasPendingSave && isCompleted)
                SaveCore();
        }
    }

    /// <summary>
    ///     保存配置到文件
    /// </summary>
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(configPath))
            return;

        if (batchUpdateDepth > 0)
        {
            hasPendingSave = true;
            return;
        }

        SaveCore();
    }

    private void Attach(string path)
    {
        configPath   = path;
        suppressSave = false;
    }

    private bool Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;

        if (!suppressSave)
            Save();

        return true;
    }

    private static LauncherSettingsV3 CreateDetachedSettings(string configPath)
    {
        var settings = new LauncherSettingsV3();
        settings.Attach(configPath);
        return settings;
    }

    private static string GetBackupPath(string configPath) =>
        configPath + ".bak";

    private static bool TryLoadSettings
    (
        string                 sourcePath,
        string                 attachPath,
        out LauncherSettingsV3 settings,
        out Exception?         exception
    )
    {
        settings  = null!;
        exception = null;

        if (!File.Exists(sourcePath))
            return false;

        try
        {
            var json = File.ReadAllText(sourcePath, Encoding.UTF8);
            settings = JsonSerializer.Deserialize<LauncherSettingsV3>(json, JsonOptions) ?? new LauncherSettingsV3();
            settings.Attach(attachPath);
            return true;
        }
        catch (Exception ex)
        {
            exception = ex;
            return false;
        }
    }

    private static string? MoveBrokenConfig(string configPath)
    {
        if (!File.Exists(configPath))
            return null;

        try
        {
            var brokenPath = $"{configPath}.broken-{DateTime.Now:yyyyMMddHHmmssfff}";
            File.Move(configPath, brokenPath);
            return brokenPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "隔离损坏配置文件失败: {ConfigPath}", configPath);
            return null;
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

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            IncludeFields               = true,
            PropertyNameCaseInsensitive = true,
            WriteIndented               = true
        };

        options.Converters.Add(new DirectoryInfoJsonConverter());
        return options;
    }

    private void SaveCore()
    {
        if (string.IsNullOrWhiteSpace(configPath))
            return;

        var currentConfigPath = configPath;
        var directoryPath     = Path.GetDirectoryName(currentConfigPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        var tempPath   = currentConfigPath + ".tmp";
        var backupPath = GetBackupPath(currentConfigPath);
        var json       = JsonSerializer.Serialize(this, JsonOptions);

        try
        {
            File.WriteAllText(tempPath, json, Utf8WithoutBom);

            if (File.Exists(currentConfigPath))
                File.Replace(tempPath, currentConfigPath, backupPath, true);
            else
                File.Move(tempPath, currentConfigPath);

            hasPendingSave = false;
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }
}
