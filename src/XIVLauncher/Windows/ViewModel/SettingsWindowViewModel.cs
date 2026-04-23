using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json.Linq;
using XIVLauncher.Accounts.Cred;
using XIVLauncher.Common;
using XIVLauncher.Common.Addon;
using XIVLauncher.Common.Addon.Implementations;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game.Integrity;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Common.Util;
using XIVLauncher.Settings;
using XIVLauncher.Support;
using XIVLauncher.Windows.Services;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel;

public sealed class SettingsWindowViewModel : INotifyPropertyChanged
{
    public List<GenericCombinedData<LauncherLanguage>> LauncherLanguageList { get; }

    public ObservableCollection<AddonEntry> AddonEntries { get; } = [];

    public ObservableCollection<CredTypeOptionItem> CredTypeOptions { get; } = [];

    public ICommand IdentifyTokenCommand => identifyTokenCommand;

    public ICommand AddAddonCommand => addAddonCommand;

    public ICommand RemoveSelectedAddonCommand => removeSelectedAddonCommand;

    public ICommand OpenGitHubCommand => openGitHubCommand;

    public ICommand OpenBackupToolCommand => openBackupToolCommand;

    public ICommand OpenOriginalLauncherCommand => openOriginalLauncherCommand;

    public ICommand OpenAdvancedSettingsCommand => openAdvancedSettingsCommand;

    private readonly AsyncCommand identifyTokenCommand;
    private readonly SyncCommand  addAddonCommand;
    private readonly SyncCommand  editSelectedAddonCommand;
    private readonly SyncCommand  removeSelectedAddonCommand;
    private readonly SyncCommand  openGitHubCommand;
    private readonly SyncCommand  openBackupToolCommand;
    private readonly SyncCommand  openOriginalLauncherCommand;
    private readonly SyncCommand  openAdvancedSettingsCommand;

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
            openBackupToolCommand.RaiseCanExecuteChanged();
            openOriginalLauncherCommand.RaiseCanExecuteChanged();
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

    public bool ExitLauncherAfterGameExit
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool KeepPatches
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool RequireDeviceProfileSetupForNewAccountLogin
    {
        get;
        set => SetProperty(ref field, value);
    }

    public decimal? DalamudInjectionDelayMs
    {
        get;
        set => SetProperty(ref field, value);
    }

    public decimal? ManualInjectDelayMs
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
    } = (int)DPIAwareness.Unaware;

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

            editSelectedAddonCommand.RaiseCanExecuteChanged();
            removeSelectedAddonCommand.RaiseCanExecuteChanged();
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

    public string CommitLabelText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    private const int BYTES_TO_MB = 1048576;

    private readonly IDialogService         _dialogService;
    private readonly IExternalLaunchService _externalLaunchService;

    private static readonly List<string> LauncherLanguageStrings =
    [
        "简体中文",
        "繁體中文"
    ];

    internal SettingsWindowViewModel(IDialogService? dialogService = null, IExternalLaunchService? externalLaunchService = null)
    {
        _dialogService         = dialogService         ?? new DialogService();
        _externalLaunchService = externalLaunchService ?? new ExternalLaunchService();

        LauncherLanguageList =
            LauncherLanguageStrings
                .Zip(Enum.GetValues<LauncherLanguage>())
                .Select(pair => new GenericCombinedData<LauncherLanguage> { Display = pair.First, Value = pair.Second })
                .ToList();

        identifyTokenCommand        = new AsyncCommand(_ => IdentifyTokenAsync());
        addAddonCommand             = new SyncCommand(_ => AddAddon());
        editSelectedAddonCommand    = new SyncCommand(_ => EditSelectedAddon(),   () => CanEditSelectedAddon);
        removeSelectedAddonCommand  = new SyncCommand(_ => RemoveSelectedAddon(), () => SelectedAddonEntry != null);
        openGitHubCommand           = new SyncCommand(_ => OpenGitHub());
        openBackupToolCommand       = new SyncCommand(_ => OpenBackupTool(),       () => !string.IsNullOrWhiteSpace(GamePath));
        openOriginalLauncherCommand = new SyncCommand(_ => OpenOriginalLauncher(), () => !string.IsNullOrWhiteSpace(GamePath));
        openAdvancedSettingsCommand = new SyncCommand(_ => OpenAdvancedSettings());

        InitializeCredTypeOptions();
        ReloadFromSettings();
    }

    public void ReloadFromSettings()
    {
        var patchPath = Paths.ResolvePatchPath(App.Settings.PatchPath, Paths.RoamingPath);

        GamePath = App.Settings.GamePath?.FullName ?? string.Empty;
        PatchPath = patchPath.FullName;

        LauncherLanguage                            = LauncherLanguage.SimplifiedChinese;
        LauncherLanguageNoticeVisible               = false;
        AskBeforePatching                           = App.Settings.AskBeforePatchInstall;
        ExitLauncherAfterGameExit                   = App.Settings.ExitLauncherWhenGameExit;
        KeepPatches                                 = App.Settings.KeepPatches;
        RequireDeviceProfileSetupForNewAccountLogin = App.Settings.RequireDeviceProfileSetupForNewLogin;
        PatchAcquisitionIndex                       = (int)App.Settings.PatchAcquisitionMethod;
        DalamudInjectionDelayMs                     = App.Settings.DalamudInjectionDelayMS;
        ManualInjectDelayMs                         = App.Settings.ManualInjectDelayMs;
        UseEntryPointLoadMethod                     = App.Settings.DalamudLoadMethod != DalamudLoadMethod.DllInject;
        EnableHooks                                 = App.Settings.DalamudEnabled;
        EnableDcTravel                              = true;
        LaunchArgs                                  = App.Settings.AdditionalLaunchArgs ?? string.Empty;
        DpiAwarenessIndex                           = (int)App.Settings.DPIAwareness;
        VersionLabelText                            = $"XIVLauncher - v{AppUtil.GetAssemblyVersion()}";
        CommitLabelText                             = $"{AppUtil.GetGitHash()}";
        SpeedLimitMb                                = (decimal)App.Settings.SpeedLimitBytes / BYTES_TO_MB;
        SelectedCredType                            = App.AccountManager.CurrentCredType;
        GitHubToken                                 = App.Settings.GitHubToken ?? string.Empty;

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

        var gamePath              = !string.IsNullOrWhiteSpace(GamePath) ? new DirectoryInfo(GamePath) : null!;
        var patchPath             = !string.IsNullOrWhiteSpace(PatchPath) ? new DirectoryInfo(PatchPath) : null!;
        var addonEntries          = AddonEntries.ToList();
        var patchAcquisitionMethod = (AcquisitionMethod)PatchAcquisitionIndex;
        var dalamudLoadMethod      = UseDllInjectLoadMethod ? DalamudLoadMethod.DllInject : DalamudLoadMethod.EntryPoint;
        var dpiAwareness           = (DPIAwareness)DpiAwarenessIndex;
        var speedLimitBytes        = (long)((SpeedLimitMb ?? 0) * BYTES_TO_MB);

        var requestedCredType   = SelectedCredType;
        var credTypeApplyResult = await App.AccountManager.ChangeCredTypeAsync(requestedCredType);

        if (!credTypeApplyResult.Succeeded)
        {
            SelectedCredType = App.AccountManager.CurrentCredType;
            await RefreshCredTypeOptionsAsync();
            _dialogService.ShowMessage
            (
                credTypeApplyResult.UserMessage ?? $"切换到 {requestedCredType.GetDisplayName()} 失败，请稍后重试。",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                false,
                false
            );
            return false;
        }

        App.Settings.Update
        (
            settings =>
            {
                settings.GamePath                            = gamePath;
                settings.PatchPath                           = patchPath;
                settings.LauncherLanguage                    = LauncherLanguage.SimplifiedChinese;
                settings.AddonList                           = addonEntries;
                settings.AskBeforePatchInstall               = AskBeforePatching;
                settings.ExitLauncherWhenGameExit            = ExitLauncherAfterGameExit;
                settings.KeepPatches                         = KeepPatches;
                settings.RequireDeviceProfileSetupForNewLogin = RequireDeviceProfileSetupForNewAccountLogin;
                settings.PatchAcquisitionMethod              = patchAcquisitionMethod;
                settings.DalamudEnabled                      = EnableHooks;
                settings.DalamudInjectionDelayMS             = DalamudInjectionDelayMs ?? 0;
                settings.ManualInjectDelayMs                 = ManualInjectDelayMs     ?? 0;
                settings.DalamudLoadMethod                   = dalamudLoadMethod;
                settings.AdditionalLaunchArgs                = LaunchArgs;
                settings.DPIAwareness                        = dpiAwareness;
                settings.SpeedLimitBytes                     = speedLimitBytes;
                settings.GitHubToken                         = GitHubToken;
                settings.CredType                            = credTypeApplyResult.AppliedCredType;
            }
        );

        SelectedCredType = credTypeApplyResult.AppliedCredType;

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

            var     response = await httpClient.GetAsync(Links.GITHUB_API_RATE_LIMIT_URL);
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
        _externalLaunchService.OpenUrl(Links.REPO_URL);

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
        new(CredType.NoEncryption, "无加密（不推荐）", true),
        new(CredType.WindowsCredManager, "系统凭据管理器", true),
        new(CredType.WindowsHello, isWindowsHelloSupported ? "Windows Hello" : "Windows Hello（当前设备不可用）", isWindowsHelloSupported)
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

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}
