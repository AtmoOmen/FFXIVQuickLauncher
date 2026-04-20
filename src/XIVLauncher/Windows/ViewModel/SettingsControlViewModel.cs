using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json.Linq;
using XIVLauncher.Accounts;
using XIVLauncher.Accounts.Cred;
using XIVLauncher.Common;
using XIVLauncher.Common.Addon;
using XIVLauncher.Common.Addon.Implementations;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Common.Util;
using XIVLauncher.Support;
using XIVLauncher.Windows.Services;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel;

public sealed class SettingsControlViewModel : ViewModelBase
{
    public List<GenericCombinedData<LauncherLanguage>> LauncherLanguageList { get; }

    public ObservableCollection<AddonEntry> AddonEntries { get; } = [];

    public ObservableCollection<CredTypeOptionItem> CredTypeOptions { get; } = [];

    public ICommand IdentifyTokenCommand { get; }

    public ICommand AddAddonCommand { get; }

    public ICommand EditSelectedAddonCommand { get; }

    public ICommand RemoveSelectedAddonCommand { get; }

    public ICommand OpenGitHubCommand { get; }

    public ICommand OpenBackupToolCommand { get; }

    public ICommand OpenOriginalLauncherCommand { get; }
    
    public ICommand OpenLicenseCommand { get; }

    public ICommand OpenAdvancedSettingsCommand { get; }

    public ICommand OpenChangelogCommand { get; }

    public ICommand OpenSharedDeviceProfileCommand { get; }

    public bool IsRunIntegrityCheckPossible =>
        !string.IsNullOrWhiteSpace(GamePath) && Directory.Exists(GamePath);

    public bool CanEditSelectedAddon => SelectedAddonEntry?.Addon != null;

    public Visibility GamePathWarningVisibility =>
        string.IsNullOrWhiteSpace(GamePathWarningMessage) ? Visibility.Collapsed : Visibility.Visible;

    public string GamePath
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            RefreshGamePathWarning();
            OnPropertyChanged(nameof(IsRunIntegrityCheckPossible));
            CommandManager.InvalidateRequerySuggested();
        }
    } = string.Empty;

    public string PatchPath
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public LauncherLanguage LauncherLanguage
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            LauncherLanguageNoticeVisible = App.Settings.LauncherLanguage != value;
        }
    } = LauncherLanguage.SimplifiedChinese;

    public bool LauncherLanguageNoticeVisible
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string GitHubToken
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public bool AskBeforePatching
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool KeepPatches
    {
        get;
        set => SetProperty(ref field, value);
    }

    public decimal? DalamudInjectionDelayMs
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool EnableHooks
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool EnableDcTravel
    {
        get;
        set
        {
            _     = value;
            field = true;
        }
    } = true;

    public string LaunchArgs
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public int DpiAwarenessIndex
    {
        get;
        set => SetProperty(ref field, value);
    } = (int)DpiAwareness.Unaware;

    public int PatchAcquisitionIndex
    {
        get;
        set => SetProperty(ref field, value);
    }

    public decimal? SpeedLimitMb
    {
        get;
        set => SetProperty(ref field, value);
    }

    public CredType SelectedCredType
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            SyncSelectedCredTypeOption();
        }
    } = CredType.WindowsCredManager;

    public CredTypeOptionItem? SelectedCredTypeOption
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || value == null || SelectedCredType == value.Value)
                return;

            SelectedCredType = value.Value;
        }
    }

    public bool UseEntryPointLoadMethod
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnPropertyChanged(nameof(UseDllInjectLoadMethod));
        }
    } = true;

    public bool UseDllInjectLoadMethod
    {
        get => !UseEntryPointLoadMethod;
        set
        {
            if (value == UseDllInjectLoadMethod)
                return;

            UseEntryPointLoadMethod = !value;
        }
    }

    public AddonEntry? SelectedAddonEntry
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string GamePathWarningMessage
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnPropertyChanged(nameof(GamePathWarningVisibility));
        }
    } = string.Empty;

    public string VersionLabelText
    {
        get;
        set => SetProperty(ref field, value);
    } = "XIVLauncher";

    private const int BytesToMb = 1048576;

    private readonly IDialogService         _dialogService;
    private readonly IExternalLaunchService _externalLaunchService;

    private static readonly List<string> LauncherLanguageStrings =
    [
        "简体中文",
        "繁體中文"
    ];

    internal SettingsControlViewModel(IDialogService? dialogService = null, IExternalLaunchService? externalLaunchService = null)
    {
        _dialogService         = dialogService         ?? new DialogService();
        _externalLaunchService = externalLaunchService ?? new ExternalLaunchService();

        LauncherLanguageList =
            LauncherLanguageStrings
                .Zip(Enum.GetValues<LauncherLanguage>())
                .Select(pair => new GenericCombinedData<LauncherLanguage> { Display = pair.First, Value = pair.Second })
                .ToList();

        IdentifyTokenCommand        = new AsyncCommand(_ => IdentifyTokenAsync());
        AddAddonCommand             = new SyncCommand(_ => AddAddon());
        EditSelectedAddonCommand    = new SyncCommand(_ => EditSelectedAddon(),   () => CanEditSelectedAddon);
        RemoveSelectedAddonCommand  = new SyncCommand(_ => RemoveSelectedAddon(), () => SelectedAddonEntry != null);
        OpenGitHubCommand           = new SyncCommand(_ => OpenGitHub());
        OpenBackupToolCommand       = new SyncCommand(_ => OpenBackupTool(),       () => !string.IsNullOrWhiteSpace(GamePath));
        OpenOriginalLauncherCommand = new SyncCommand(_ => OpenOriginalLauncher(), () => !string.IsNullOrWhiteSpace(GamePath));
        OpenLicenseCommand          = new SyncCommand(_ => OpenLicense());
        OpenAdvancedSettingsCommand = new SyncCommand(_ => OpenAdvancedSettings());
        OpenChangelogCommand        = new SyncCommand(_ => OpenChangelog());
        OpenSharedDeviceProfileCommand = new SyncCommand(_ => OpenSharedDeviceProfile());

        InitializeCredTypeOptions();
        ReloadFromSettings();
    }

    public void ReloadFromSettings()
    {
        GamePath = App.Settings.GamePath?.FullName ?? string.Empty;

        if (App.Settings.PatchPath is { Exists: false })
            App.Settings.PatchPath = null!;

        App.Settings.PatchPath ??= new DirectoryInfo(Path.Combine(Paths.RoamingPath, "patches"));
        PatchPath              =   App.Settings.PatchPath?.FullName ?? string.Empty;

        LauncherLanguage              = LauncherLanguage.SimplifiedChinese;
        LauncherLanguageNoticeVisible = false;
        AskBeforePatching             = App.Settings.AskBeforePatchInstall ?? true;
        KeepPatches                   = App.Settings.KeepPatches           ?? false;
        PatchAcquisitionIndex         = (int)App.Settings.PatchAcquisitionMethod.GetValueOrDefault(AcquisitionMethod.Aria);
        DalamudInjectionDelayMs       = App.Settings.DalamudInjectionDelayMs;
        UseEntryPointLoadMethod       = App.Settings.InGameAddonLoadMethod != DalamudLoadMethod.DllInject;
        EnableHooks                   = App.Settings.InGameAddonEnabled;
        EnableDcTravel                = true;
        LaunchArgs                    = App.Settings.AdditionalLaunchArgs ?? string.Empty;
        DpiAwarenessIndex             = (int)App.Settings.DpiAwareness.GetValueOrDefault(DpiAwareness.Unaware);
        VersionLabelText              = $"XIVLauncher - v{AppUtil.GetAssemblyVersion()} - {AppUtil.GetGitHash()} - {Environment.Version}";
        SpeedLimitMb                  = (decimal)App.Settings.SpeedLimitBytes / BytesToMb;
        SelectedCredType             = App.AccountManager.CurrentCredType;
        GitHubToken                   = App.Settings.GitHubToken ?? string.Empty;

        ReplaceAddonEntries(App.Settings.AddonList ?? []);
        RefreshGamePathWarning();
        _ = RefreshCredTypeOptionsAsync();
    }

    public async Task<bool> SaveToSettingsAsync()
    {
        if (string.Equals(GamePath, PatchPath, StringComparison.OrdinalIgnoreCase))
        {
            _dialogService.ShowMessage
            (
                "游戏目录和补丁目录不能相同，请重新选择。",
                "XIVLauncher 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            return false;
        }

        App.Settings.GamePath                = !string.IsNullOrWhiteSpace(GamePath) ? new DirectoryInfo(GamePath) : null!;
        App.Settings.PatchPath               = !string.IsNullOrWhiteSpace(PatchPath) ? new DirectoryInfo(PatchPath) : null!;
        App.Settings.LauncherLanguage        = LauncherLanguage.SimplifiedChinese;
        App.Settings.AddonList               = AddonEntries.ToList();
        App.Settings.AskBeforePatchInstall   = AskBeforePatching;
        App.Settings.KeepPatches             = KeepPatches;
        App.Settings.PatchAcquisitionMethod  = (AcquisitionMethod)PatchAcquisitionIndex;
        App.Settings.InGameAddonEnabled      = EnableHooks;
        App.Settings.DalamudInjectionDelayMs = DalamudInjectionDelayMs ?? 0;
        App.Settings.InGameAddonLoadMethod   = UseDllInjectLoadMethod ? DalamudLoadMethod.DllInject : DalamudLoadMethod.EntryPoint;
        App.Settings.AdditionalLaunchArgs    = LaunchArgs;
        App.Settings.DpiAwareness            = (DpiAwareness)DpiAwarenessIndex;
        App.Settings.SpeedLimitBytes         = (long)((SpeedLimitMb ?? 0) * BytesToMb);
        App.Settings.GitHubToken             = GitHubToken;

        var requestedCredType   = SelectedCredType;
        var credTypeApplyResult = await App.AccountManager.ChangeCredTypeAsync(requestedCredType);
        if (!credTypeApplyResult.Succeeded)
        {
            SelectedCredType = App.AccountManager.CurrentCredType;
            await RefreshCredTypeOptionsAsync();
            _dialogService.ShowMessage
            (
                credTypeApplyResult.UserMessage ?? $"切换到 {AccountManager.GetCredTypeDisplayName(requestedCredType)} 失败，请稍后重试。",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                false,
                false
            );
            return false;
        }

        App.Settings.CredType = credTypeApplyResult.AppliedCredType;
        SelectedCredType      = credTypeApplyResult.AppliedCredType;

        SettingsSaved?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task IdentifyTokenAsync()
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");

            if (!string.IsNullOrWhiteSpace(GitHubToken))
                httpClient.DefaultRequestHeaders.Authorization = new("Bearer", GitHubToken);

            var     response = await httpClient.GetAsync("https://api.github.com/rate_limit");
            var     json     = await response.Content.ReadAsStringAsync();
            dynamic parsed   = JObject.Parse(json);

            if (!response.IsSuccessStatusCode)
            {
                _dialogService.ShowMessage
                (
                    $"获取 GitHub API 额度失败，请检查 Token 是否正确。\n{parsed.message}",
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            int remaining      = parsed.resources.core.remaining;
            int limit          = parsed.resources.core.limit;
            int resetTimestamp = parsed.resources.core.reset;
            var resetTime      = DateTimeOffset.FromUnixTimeSeconds(resetTimestamp).LocalDateTime;
            var sourceText     = string.IsNullOrWhiteSpace(GitHubToken) ? "当前 IP" : "当前 Token";

            _dialogService.ShowMessage
            (
                $"{sourceText} 的可用额度为 {remaining} / {limit}，重置时间：{resetTime:HH:mm:ss}",
                "XIVLauncherCN (Soil)"
            );
        }
        catch (Exception ex)
        {
            _dialogService.ShowMessage
            (
                $"获取 GitHub API 额度失败。\n{ex}",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
    }

    public void AddAddon()
    {
        var result = _dialogService.ShowGenericAddonSetup();
        if (result == null || string.IsNullOrWhiteSpace(result.Path))
            return;

        AddonEntries.Add
        (
            new AddonEntry
            {
                IsEnabled = true,
                Addon     = result
            }
        );
    }

    public void EditSelectedAddon()
    {
        if (SelectedAddonEntry?.Addon is not GenericAddon genericAddon)
            return;

        var index  = AddonEntries.IndexOf(SelectedAddonEntry);
        var result = _dialogService.ShowGenericAddonSetup(genericAddon);
        if (result == null || index < 0)
            return;

        AddonEntries[index] = new AddonEntry
        {
            IsEnabled = SelectedAddonEntry.IsEnabled,
            Addon     = result
        };
        SelectedAddonEntry = AddonEntries[index];
    }

    public void RemoveSelectedAddon()
    {
        if (SelectedAddonEntry == null)
            return;

        AddonEntries.Remove(SelectedAddonEntry);
        SelectedAddonEntry = null;
    }

    public void OpenGitHub() =>
        _externalLaunchService.OpenUrl("https://github.com/AtmoOmen/FFXIVQuickLauncher");

    public void OpenBackupTool()
    {
        if (string.IsNullOrWhiteSpace(GamePath))
            return;

        _externalLaunchService.OpenExecutable(Path.Combine(GamePath, "boot", "ffxivconfig64.exe"));
    }

    public void OpenOriginalLauncher()
    {
        var gamePath = !string.IsNullOrWhiteSpace(GamePath) ? new DirectoryInfo(GamePath) : App.Settings.GamePath;
        GameHelpers.StartOfficialLauncher(gamePath);
    }

    public void OpenLicense() =>
        _externalLaunchService.OpenPath(Path.Combine(Paths.ResourcesPath, "LICENSE.txt"));

    public void OpenAdvancedSettings() =>
        _dialogService.ShowAdvancedSettings();

    public void OpenChangelog()
    {
        var version = AppUtil.GetAssemblyVersion();
        if (!string.IsNullOrWhiteSpace(version))
            _dialogService.ShowChangelog(version);
    }

    public void OpenSharedDeviceProfile() =>
        _dialogService.ShowSharedDeviceProfileSettings(App.AccountManager);

    public async Task<IntegrityCheckCompareOutcome?> RunIntegrityCheckAsync(IProgress<IntegrityCheckProgress> progress)
    {
        var gamePath = ValidateGamePathForIntegrityCheck();
        if (gamePath == null)
            return null;

        var result     = await IntegrityCheck.CompareIntegrityAsync(progress, gamePath);
        var reportPath = Path.Combine(Paths.RoamingPath, "integrityreport.txt");
        File.WriteAllText(reportPath, result.report);

        return new IntegrityCheckCompareOutcome(result.compareResult, reportPath);
    }

    public async Task<string?> GenerateIntegrityCheckAsync(IProgress<IntegrityCheckProgress> progress)
    {
        var gamePath = ValidateGamePathForIntegrityCheck();
        if (gamePath == null)
            return null;

        return await IntegrityCheck.GenerateIntegrityAsync(progress, gamePath);
    }

    public void ShowIntegrityCheckResult(IntegrityCheckCompareOutcome outcome)
    {
        switch (outcome.CompareResult)
        {
            case IntegrityCheckCompareResult.ReferenceNotFound:
                _dialogService.ShowMessage("当前游戏版本还没有可用的参考报告，请稍后再试。", "XIVLauncherCN (Soil)");
                return;

            case IntegrityCheckCompareResult.ReferenceFetchFailure:
                _dialogService.ShowMessage
                (
                    "下载完整性检查参考文件失败，请检查网络连接后重试。",
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                return;

            case IntegrityCheckCompareResult.Invalid:
                _dialogService.ShowMessage
                (
                    "检测到部分游戏文件可能已被修改或损坏。\n\n如果你使用了 TexTools 等模组工具，这通常是预期结果。\n\n如果没有使用模组，可以在登录按钮的右键菜单中选择“修复游戏”。",
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation,
                    showReportLinks: true
                );
                return;

            case IntegrityCheckCompareResult.Valid:
                _dialogService.ShowMessage("游戏安装完整。", "XIVLauncherCN (Soil)");
                return;
        }
    }

    public void ShowGeneratedIntegrityCheckResult(string outputPath) =>
        _dialogService.ShowMessage
        (
            $"已完成游戏客户端 Hash 数据生成，文件保存在：\n{outputPath}",
            "XIVLauncherCN (Soil)"
        );

    private DirectoryInfo? ValidateGamePathForIntegrityCheck()
    {
        if (string.IsNullOrWhiteSpace(GamePath))
        {
            _dialogService.ShowMessage("请先选择游戏目录。", "XIVLauncherCN (Soil)");
            return null;
        }

        var gamePath = new DirectoryInfo(GamePath);

        if (Repository.Ffxiv.IsBaseVer(gamePath))
        {
            _dialogService.ShowMessage("所选路径中没有检测到游戏安装，请先确认游戏目录。", "XIVLauncherCN (Soil)");
            return null;
        }

        return gamePath;
    }

    private void InitializeCredTypeOptions() =>
        ReplaceCredTypeOptions(BuildCredTypeOptions(true));

    private async Task RefreshCredTypeOptionsAsync()
    {
        var isWindowsHelloSupported = true;

        try
        {
            isWindowsHelloSupported = await App.AccountManager.IsCredTypeSupportedAsync(CredType.WindowsHello);
        }
        catch
        {
            isWindowsHelloSupported = true;
        }

        ReplaceCredTypeOptions(BuildCredTypeOptions(isWindowsHelloSupported));

        if (!CredTypeOptions.Any(option => option.Value == SelectedCredType && option.IsEnabled))
            SelectedCredType = App.AccountManager.CurrentCredType;
    }

    private void ReplaceCredTypeOptions(IEnumerable<CredTypeOptionItem> options)
    {
        CredTypeOptions.Clear();

        foreach (var option in options)
            CredTypeOptions.Add(option);

        SyncSelectedCredTypeOption();
    }

    private void SyncSelectedCredTypeOption()
    {
        var selectedOption = CredTypeOptions.FirstOrDefault(option => option.Value == SelectedCredType);

        if (!EqualityComparer<CredTypeOptionItem?>.Default.Equals(SelectedCredTypeOption, selectedOption))
            SelectedCredTypeOption = selectedOption;
    }

    private static IReadOnlyList<CredTypeOptionItem> BuildCredTypeOptions(bool isWindowsHelloSupported) =>
    [
        new(CredType.NoEncryption,       "无加密（不推荐）",                                             true),
        new(CredType.WindowsCredManager, "系统凭据管理器",                                               true),
        new(CredType.WindowsHello,       isWindowsHelloSupported ? "Windows Hello" : "Windows Hello（当前设备不可用）", isWindowsHelloSupported)
    ];

    private void ReplaceAddonEntries(IEnumerable<AddonEntry> entries)
    {
        AddonEntries.Clear();
        foreach (var entry in entries)
            AddonEntries.Add(entry);
    }

    private void RefreshGamePathWarning()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(GamePath) && !GameHelpers.LetChoosePath(GamePath))
                GamePathWarningMessage = "请选择游戏根目录，不要直接选到 Game 或 boot 子目录。";
            else if (!string.IsNullOrWhiteSpace(GamePath) && GameHelpers.CanMightNotBeInternationalClient(GamePath) && App.Settings.Language != ClientLanguage.ChineseSimplified)
                GamePathWarningMessage = "当前路径看起来不像国际服客户端，请确认选择的是正确目录。";
            else
                GamePathWarningMessage = string.Empty;
        }
        catch
        {
            GamePathWarningMessage = string.Empty;
        }
    }

    public event EventHandler? SettingsSaved;

    public sealed record IntegrityCheckCompareOutcome
    (
        IntegrityCheckCompareResult CompareResult,
        string                      ReportPath
    );

    public sealed record CredTypeOptionItem
    (
        CredType Value,
        string   Display,
        bool     IsEnabled
    );
}
