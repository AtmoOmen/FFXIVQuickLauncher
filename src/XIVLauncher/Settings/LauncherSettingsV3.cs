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

public sealed class LauncherSettingsV3
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private string? configPath;
    private bool    suppressSave = true;

    #region Launcher Setting

    public DirectoryInfo GamePath
    {
        get;
        set => Set(ref field, value);
    } = null!;

    public List<AddonEntry> AddonList
    {
        get;
        set
        {
            value = value.Where(x => !string.IsNullOrEmpty(x.Addon.Path)).ToList();
            Set(ref field, value);
        }
    } = [];

    public string AdditionalLaunchArgs
    {
        get;
        set => Set(ref field, value);
    } = string.Empty;

    public bool DalamudEnabled
    {
        get;
        set => Set(ref field, value);
    } = true;

    public DalamudLoadMethod DalamudLoadMethod
    {
        get;
        set => Set(ref field, value);
    } = DalamudLoadMethod.EntryPoint;

    public ClientLanguage Language
    {
        get;
        set => Set(ref field, value);
    } = ClientLanguage.ChineseSimplified;

    public LauncherLanguage LauncherLanguage
    {
        get;
        set => Set(ref field, value);
    } = LauncherLanguage.SimplifiedChinese;

    public string CurrentAccountID
    {
        get;
        set => Set(ref field, value);
    } = string.Empty;

    public bool EncryptArgumentsV2
    {
        get;
        set => Set(ref field, value);
    } = true;

    public DirectoryInfo PatchPath
    {
        get;
        set => Set(ref field, value);
    } = null!;

    public bool AskBeforePatchInstall
    {
        get;
        set => Set(ref field, value);
    } = true;

    public long SpeedLimitBytes
    {
        get;
        set => Set(ref field, value);
    } = 0;

    public decimal DalamudInjectionDelayMS
    {
        get;
        set => Set(ref field, value);
    } = 0;

    public bool ManualInjectAutoInjectEnabled
    {
        get;
        set => Set(ref field, value);
    }

    public bool RequireDeviceProfileSetupForNewLogin
    {
        get;
        set => Set(ref field, value);
    }

    public decimal ManualInjectDelayMs
    {
        get;
        set => Set(ref field, value);
    } = 0;

    public bool KeepPatches
    {
        get;
        set => Set(ref field, value);
    }

    public bool HasComplainedAboutGShadeDXGI
    {
        get;
        set => Set(ref field, value);
    }

    public AcquisitionMethod PatchAcquisitionMethod
    {
        get;
        set => Set(ref field, value);
    } = AcquisitionMethod.Aria;

    public string GitHubToken
    {
        get;
        set => Set(ref field, value);
    } = string.Empty;

    public DPIAwareness DPIAwareness
    {
        get;
        set => Set(ref field, value);
    } = DPIAwareness.Aware;

    public int VersionUpgradeLevel
    {
        get;
        set => Set(ref field, value);
    } = 0;

    public bool TreatNonZeroExitCodeAsFailure
    {
        get;
        set => Set(ref field, value);
    }

    public bool ExitLauncherWhenGameExit
    {
        get;
        set => Set(ref field, value);
    }

    public PreserveWindowPosition.WindowPlacement? MainWindowPlacement
    {
        get;
        set => Set(ref field, value);
    } = null;

    public LoginType SelectedLoginType
    {
        get;
        set => Set(ref field, value);
    } = LoginType.Slide;

    public int SelectedServer
    {
        get;
        set => Set(ref field, value);
    } = 0;

    public bool FastLogin
    {
        get;
        set => Set(ref field, value);
    } = true;

    public CredType CredType
    {
        get;
        set => Set(ref field, value);
    } = CredType.WindowsCredManager;

    public bool EnableSkipUpdate
    {
        get;
        set => Set(ref field, value);
    }

    public bool EnableVerboseLog
    {
        get;
        set => Set(ref field, value);
    }

    #endregion

    public static LauncherSettingsV3 Load(string configPath)
    {
        if (!File.Exists(configPath))
        {
            var created = new LauncherSettingsV3();
            created.Attach(configPath);
            return created;
        }

        var json = File.ReadAllText(configPath, Encoding.UTF8);
        var settings = JsonSerializer.Deserialize<LauncherSettingsV3>(json, JsonOptions) ?? new LauncherSettingsV3();
        settings.Attach(configPath);
        return settings;
    }

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
            IncludeFields              = true,
            PropertyNameCaseInsensitive = true,
            WriteIndented              = true
        };

        options.Converters.Add(new DirectoryInfoJsonConverter());
        return options;
    }
}
