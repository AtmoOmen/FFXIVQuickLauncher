using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVLauncher.Account.Cred;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Util;
using XIVLauncher.CompanionApp;
using XIVLauncher.Dalamud;
using XIVLauncher.Login.WeGame;
using XIVLauncher.Windows.Services;

namespace XIVLauncher.Windows.ViewModel;

public sealed partial class SettingsWindowViewModel : ObservableObject
{
    public ObservableCollection<CompanionAppEntry>  CompanionAppEntries { get; } = [];
    public ObservableCollection<CredTypeOptionItem> CredTypeOptions     { get; } = [];

    public bool CanEditSelectedCompanionApp =>
        SelectedCompanionAppEntry?.CompanionApp != null;

    public Visibility GamePathWarningVisibility =>
        string.IsNullOrWhiteSpace(GamePathWarningMessage) ?
            Visibility.Collapsed :
            Visibility.Visible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenBackupToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenOriginalLauncherCommand))]
    public partial string GamePath { get; set; } = string.Empty;

    partial void OnGamePathChanged
    (
        string value
    ) =>
        RefreshGamePathWarning();

    [ObservableProperty]
    public partial string PatchPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WeGamePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AskBeforePatching { get; set; }

    [ObservableProperty]
    public partial bool ExitLauncherAfterGameExit { get; set; }

    [ObservableProperty]
    public partial bool KeepPatches { get; set; }

    [ObservableProperty]
    public partial bool RequireDeviceProfileSetupForNewAccountLogin { get; set; }

    [ObservableProperty]
    public partial bool RequireDeviceProfileSetupForQRCodeLogin { get; set; }

    public decimal? DalamudInjectionDelayMs
    {
        get;
        set
        {
            var clamped = value.HasValue ?
                              Math.Clamp(value.Value, 0, DalamudLaunchOptions.MAX_DELAY_INITIALIZE_MS) :
                              value;

            // 即便钳制后与当前值相同, 也要刷新 TextBox, 把超限的输入回退到上限
            if (!SetProperty(ref field, clamped) && value != clamped)
                OnPropertyChanged();
        }
    }

    [ObservableProperty]
    public partial decimal? ManualInjectDelayMs { get; set; }

    [ObservableProperty]
    public partial bool EnableHooks { get; set; }

    public bool EnableDcTravel
    {
        get;
        set
        {
            _     = value;
            field = true;
        }
    } = true;

    [ObservableProperty]
    public partial string LaunchArgs { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int DpiAwarenessIndex { get; set; } = (int)DPIAwareness.Unaware;

    [ObservableProperty]
    public partial decimal? SpeedLimitMb { get; set; }

    [ObservableProperty]
    public partial CredType SelectedCredType { get; set; } = CredType.WindowsCredManager;

    partial void OnSelectedCredTypeChanged
    (
        CredType value
    ) =>
        SyncSelectedCredTypeOption();

    [ObservableProperty]
    public partial CredTypeOptionItem? SelectedCredTypeOption { get; set; }

    partial void OnSelectedCredTypeOptionChanged
    (
        CredTypeOptionItem? value
    )
    {
        if (value == null || SelectedCredType == value.Value)
            return;

        SelectedCredType = value.Value;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedCompanionAppCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCompanionAppCommand))]
    public partial CompanionAppEntry? SelectedCompanionAppEntry { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GamePathWarningVisibility))]
    public partial string GamePathWarningMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VersionLabelText { get; set; } = "XIVLauncher";

    [ObservableProperty]
    public partial string CommitLabelText { get; set; } = string.Empty;

    private const int BYTES_TO_MB = 1048576;

    private readonly IDialogService         _dialogService;
    private readonly IExternalLaunchService _externalLaunchService;

    internal SettingsWindowViewModel
    (
        IDialogService?         dialogService         = null,
        IExternalLaunchService? externalLaunchService = null
    )
    {
        _dialogService         = dialogService         ?? new DialogService();
        _externalLaunchService = externalLaunchService ?? new ExternalLaunchService();

        InitializeCredTypeOptions();
        ReloadFromSettings();
    }

    [RelayCommand]
    private void AddCompanionApp()
    {
        var result = _dialogService.ShowCompanionAppSetup();
        if (result == null || string.IsNullOrWhiteSpace(result.FilePath))
            return;

        CompanionAppEntries.Add
        (
            new CompanionAppEntry
            {
                IsEnabled    = true,
                CompanionApp = result
            }
        );
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedCompanionApp))]
    private void EditSelectedCompanionApp()
    {
        if (SelectedCompanionAppEntry?.CompanionApp is not { } companionApp)
            return;

        var index  = CompanionAppEntries.IndexOf(SelectedCompanionAppEntry);
        var result = _dialogService.ShowCompanionAppSetup(companionApp);
        if (result == null || index < 0)
            return;

        CompanionAppEntries[index] = new CompanionAppEntry
        {
            IsEnabled    = SelectedCompanionAppEntry.IsEnabled,
            CompanionApp = result
        };
        SelectedCompanionAppEntry = CompanionAppEntries[index];
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedCompanionApp))]
    private void RemoveSelectedCompanionApp()
    {
        if (SelectedCompanionAppEntry == null)
            return;

        CompanionAppEntries.Remove(SelectedCompanionAppEntry);
        SelectedCompanionAppEntry = null;
    }

    private bool CanRemoveSelectedCompanionApp() =>
        SelectedCompanionAppEntry != null;

    [RelayCommand]
    private void OpenGitHub() =>
        _externalLaunchService.OpenUrl(Links.REPO_URL);

    [RelayCommand(CanExecute = nameof(CanOpenGamePathCommand))]
    private void OpenBackupTool()
    {
        if (string.IsNullOrWhiteSpace(GamePath))
            return;

        _externalLaunchService.OpenExecutable(Path.Combine(GamePath, "boot", "ffxivconfig64.exe"));
    }

    [RelayCommand(CanExecute = nameof(CanOpenGamePathCommand))]
    private void OpenOriginalLauncher()
    {
        var gamePath = !string.IsNullOrWhiteSpace(GamePath) ?
                           new DirectoryInfo(GamePath) :
                           App.Settings.GamePath;
        GameHelpers.StartOfficialLauncher(gamePath);
    }

    private bool CanOpenGamePathCommand() =>
        !string.IsNullOrWhiteSpace(GamePath);

    [RelayCommand]
    private void OpenAdvancedSettings() =>
        _dialogService.ShowAdvancedSettings();

    public void OpenLicense() =>
        _externalLaunchService.OpenPath(Path.Combine(Paths.ResourcesPath, "LICENSE.txt"));

    public void OpenChangelog()
    {
        var version = AppUtil.GetAssemblyVersion();
        if (!string.IsNullOrWhiteSpace(version))
            _dialogService.ShowChangelog(version);
    }

    public void OpenSharedDeviceProfile() =>
        _dialogService.ShowSharedDeviceProfileSettings(App.AccountManager);

    public void ReloadFromSettings()
    {
        var patchPath = Paths.ResolvePatchPath(App.Settings.PatchPath, Paths.RoamingPath);

        GamePath   = App.Settings.GamePath?.FullName ?? string.Empty;
        PatchPath  = patchPath.FullName;
        WeGamePath = App.Settings.WeGamePath?.FullName ?? string.Empty;

        AskBeforePatching                           = App.Settings.AskBeforePatchInstall;
        ExitLauncherAfterGameExit                   = App.Settings.ExitLauncherWhenGameExit;
        KeepPatches                                 = App.Settings.KeepPatches;
        RequireDeviceProfileSetupForNewAccountLogin = App.Settings.RequireDeviceProfileSetupForNewLogin;
        RequireDeviceProfileSetupForQRCodeLogin     = App.Settings.RequireDeviceProfileSetupForQRCodeLogin;
        DalamudInjectionDelayMs                     = App.Settings.DalamudInjectionDelayMS;
        ManualInjectDelayMs                         = App.Settings.ManualInjectDelayMs;
        EnableHooks                                 = App.Settings.DalamudEnabled;
        EnableDcTravel                              = true;
        LaunchArgs                                  = App.Settings.AdditionalLaunchArgs ?? string.Empty;
        DpiAwarenessIndex                           = (int)App.Settings.DPIAwareness;
        VersionLabelText                            = $"XIVLauncher - v{AppUtil.GetAssemblyVersion()}";
        CommitLabelText                             = $"{AppUtil.GetGitHash()}";
        SpeedLimitMb                                = (decimal)App.Settings.SpeedLimitBytes / BYTES_TO_MB;
        SelectedCredType                            = App.AccountManager.CurrentCredType;

        ReplaceCompanionAppEntries(App.Settings.CompanionAppList ?? []);
        RefreshGamePathWarning();
        _ = RefreshCredTypeOptionsAsync();
    }

    public async Task<bool> SaveToSettingsAsync()
    {
        if (string.Equals(GamePath,   PatchPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(WeGamePath, PatchPath, StringComparison.OrdinalIgnoreCase))
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

        if (!string.IsNullOrWhiteSpace(WeGamePath) &&
            (!WeGamePathValidator.IsValidGameRoot(WeGamePath) ||
             !WeGamePathValidator.IsValidSdologinDir(WeGamePathValidator.DeriveSdologinDir(WeGamePath))))
        {
            _dialogService.ShowMessage
            (
                "未找到 WeGame 游戏标记或 sdologin.exe, 请重新选择游戏根目录。",
                "XIVLauncher 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            return false;
        }

        var gamePath = !string.IsNullOrWhiteSpace(GamePath) ?
                           new DirectoryInfo(GamePath) :
                           null;
        var patchPath = !string.IsNullOrWhiteSpace(PatchPath) ?
                            new DirectoryInfo(PatchPath) :
                            null!;
        var companionAppEntries = CompanionAppEntries.ToList();
        var dpiAwareness        = (DPIAwareness)DpiAwarenessIndex;
        var speedLimitBytes     = (long)((SpeedLimitMb ?? 0) * BYTES_TO_MB);

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
        (settings =>
            {
                settings.GamePath                                = gamePath;
                settings.PatchPath                               = patchPath;
                settings.CompanionAppList                        = companionAppEntries;
                settings.AskBeforePatchInstall                   = AskBeforePatching;
                settings.ExitLauncherWhenGameExit                = ExitLauncherAfterGameExit;
                settings.KeepPatches                             = KeepPatches;
                settings.RequireDeviceProfileSetupForNewLogin    = RequireDeviceProfileSetupForNewAccountLogin;
                settings.RequireDeviceProfileSetupForQRCodeLogin = RequireDeviceProfileSetupForQRCodeLogin;
                settings.DalamudEnabled                          = EnableHooks;
                settings.DalamudInjectionDelayMS                 = DalamudInjectionDelayMs ?? 0;
                settings.ManualInjectDelayMs                     = ManualInjectDelayMs     ?? 0;
                settings.DalamudLoadMethod                       = DalamudLoadMethod.EntryPoint;
                settings.AdditionalLaunchArgs                    = LaunchArgs;
                settings.DPIAwareness                            = dpiAwareness;
                settings.SpeedLimitBytes                         = speedLimitBytes;
                settings.WeGamePath = string.IsNullOrWhiteSpace(WeGamePath) ?
                                          null :
                                          new DirectoryInfo(WeGamePath);
                settings.CredType = credTypeApplyResult.AppliedCredType;
            }
        );

        SelectedCredType = credTypeApplyResult.AppliedCredType;

        SettingsSaved?.Invoke(this, EventArgs.Empty);
        return true;
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

    private void ReplaceCredTypeOptions
    (
        IEnumerable<CredTypeOptionItem> options
    )
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

    private static IReadOnlyList<CredTypeOptionItem> BuildCredTypeOptions
    (
        bool isWindowsHelloSupported
    ) =>
    [
        new(CredType.NoEncryption, "无加密（不推荐）", true),
        new(CredType.WindowsCredManager, "系统凭据管理器", true),
        new
        (
            CredType.WindowsHello,
            isWindowsHelloSupported ?
                "Windows Hello" :
                "Windows Hello（当前设备不可用）",
            isWindowsHelloSupported
        )
    ];

    private void ReplaceCompanionAppEntries
    (
        IEnumerable<CompanionAppEntry> entries
    )
    {
        CompanionAppEntries.Clear();
        foreach (var entry in entries)
            CompanionAppEntries.Add(entry);
    }

    private void RefreshGamePathWarning()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(GamePath) && !GameHelpers.LetChoosePath(GamePath))
                GamePathWarningMessage = "请选择游戏根目录，不要直接选到 Game 或 boot 子目录";
            else
                GamePathWarningMessage = string.Empty;
        }
        catch
        {
            GamePathWarningMessage = string.Empty;
        }
    }

    public event EventHandler? SettingsSaved;

    public sealed record CredTypeOptionItem
    (
        CredType Value,
        string   Display,
        bool     IsEnabled
    );
}
