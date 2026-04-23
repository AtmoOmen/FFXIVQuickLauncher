using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using XIVLauncher.Accounts.Cred;
using XIVLauncher.Common;
using XIVLauncher.Common.Addon;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Settings.Converters;
using XIVLauncher.Xaml;

namespace XIVLauncher.Settings;

/// <summary>
///     启动器配置 V3 版本
/// </summary>
public sealed class LauncherSettingsV3
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private string? configPath;
    private bool    suppressSave = true;

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
    ///     游戏客户端语言
    /// </summary>
    public ClientLanguage Language
    {
        get;
        set => Set(ref field, value);
    } = ClientLanguage.ChineseSimplified;

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
    public List<AddonEntry> AddonList
    {
        get;
        set
        {
            value = value.Where(x => !string.IsNullOrEmpty(x.Addon.Path)).ToList();
            Set(ref field, value);
        }
    } = [];

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
    ///     补丁获取方式
    /// </summary>
    public AcquisitionMethod PatchAcquisitionMethod
    {
        get;
        set => Set(ref field, value);
    } = AcquisitionMethod.Aria;

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
    ///     启动器界面语言
    /// </summary>
    public LauncherLanguage LauncherLanguage
    {
        get;
        set => Set(ref field, value);
    } = LauncherLanguage.SimplifiedChinese;

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
    ///     GitHub Token，用于 API 访问
    /// </summary>
    public string GitHubToken
    {
        get;
        set => Set(ref field, value);
    } = string.Empty;

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
        if (!File.Exists(configPath))
        {
            var created = new LauncherSettingsV3();
            created.Attach(configPath);
            return created;
        }

        var json     = File.ReadAllText(configPath, Encoding.UTF8);
        var settings = JsonSerializer.Deserialize<LauncherSettingsV3>(json, JsonOptions) ?? new LauncherSettingsV3();
        settings.Attach(configPath);
        return settings;
    }

    /// <summary>
    ///     保存配置到文件
    /// </summary>
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(configPath))
            return;

        var directoryPath = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        File.WriteAllText(configPath, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
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
}
