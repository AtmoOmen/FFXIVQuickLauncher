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
using XIVLauncher.Account.Cred;
using XIVLauncher.Common;
using XIVLauncher.CompanionApp;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Util;
using XIVLauncher.Dalamud;
using XIVLauncher.Windows.Services;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel;

public sealed class SettingsWindowViewModel : INotifyPropertyChanged
{
    public ObservableCollection<CompanionAppEntry> CompanionAppEntries { get; } = [];

    public ObservableCollection<CredTypeOptionItem> CredTypeOptions { get; } = [];
    
    public ICommand AddCompanionAppCommand => addCompanionAppCommand;

    public ICommand RemoveSelectedCompanionAppCommand => removeSelectedCompanionAppCommand;

    public ICommand OpenGitHubCommand => openGitHubCommand;

    public ICommand OpenBackupToolCommand => openBackupToolCommand;

    public ICommand OpenOriginalLauncherCommand => openOriginalLauncherCommand;

    public ICommand OpenAdvancedSettingsCommand => openAdvancedSettingsCommand;

    private readonly SyncCommand  addCompanionAppCommand;
    private readonly SyncCommand  editSelectedCompanionAppCommand;
    private readonly SyncCommand  removeSelectedCompanionAppCommand;
    private readonly SyncCommand  openGitHubCommand;
    private readonly SyncCommand  openBackupToolCommand;
    private readonly SyncCommand  openOriginalLauncherCommand;
    private readonly SyncCommand  openAdvancedSettingsCommand;

    public bool CanEditSelectedCompanionApp => SelectedCompanionAppEntry?.CompanionApp != null;

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
            openBackupToolCommand.RaiseCanExecuteChanged();
            openOriginalLauncherCommand.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    public string PatchPath
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

    public CompanionAppEntry? SelectedCompanionAppEntry
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            editSelectedCompanionAppCommand.RaiseCanExecuteChanged();
            removeSelectedCompanionAppCommand.RaiseCanExecuteChanged();
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

    internal SettingsWindowViewModel(IDialogService? dialogService = null, IExternalLaunchService? externalLaunchService = null)
    {
        _dialogService         = dialogService         ?? new DialogService();
        _externalLaunchService = externalLaunchService ?? new ExternalLaunchService();

        addCompanionAppCommand             = new SyncCommand(_ => AddCompanionApp());
        editSelectedCompanionAppCommand    = new SyncCommand(_ => EditSelectedCompanionApp(),   () => CanEditSelectedCompanionApp);
        removeSelectedCompanionAppCommand  = new SyncCommand(_ => RemoveSelectedCompanionApp(), () => SelectedCompanionAppEntry != null);
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

        GamePath  = App.Settings.GamePath?.FullName ?? string.Empty;
        PatchPath = patchPath.FullName;

        AskBeforePatching                           = App.Settings.AskBeforePatchInstall;
        ExitLauncherAfterGameExit                   = App.Settings.ExitLauncherWhenGameExit;
        KeepPatches                                 = App.Settings.KeepPatches;
        RequireDeviceProfileSetupForNewAccountLogin = App.Settings.RequireDeviceProfileSetupForNewLogin;
        DalamudInjectionDelayMs                     = App.Settings.DalamudInjectionDelayMS;
        ManualInjectDelayMs                         = App.Settings.ManualInjectDelayMs;
        UseEntryPointLoadMethod                     = App.Settings.DalamudLoadMethod == DalamudLoadMethod.EntryPoint;
        UseDllInjectLoadMethod                      = App.Settings.DalamudLoadMethod == DalamudLoadMethod.DllInject;
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

        var gamePath            = !string.IsNullOrWhiteSpace(GamePath) ? new DirectoryInfo(GamePath) : null!;
        var patchPath           = !string.IsNullOrWhiteSpace(PatchPath) ? new DirectoryInfo(PatchPath) : null!;
        var companionAppEntries = CompanionAppEntries.ToList();
        var dalamudLoadMethod   = App.Settings.DalamudLoadMethod;
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
                settings.GamePath                             = gamePath;
                settings.PatchPath                            = patchPath;
                settings.CompanionAppList                     = companionAppEntries;
                settings.AskBeforePatchInstall                = AskBeforePatching;
                settings.ExitLauncherWhenGameExit             = ExitLauncherAfterGameExit;
                settings.KeepPatches                          = KeepPatches;
                settings.RequireDeviceProfileSetupForNewLogin = RequireDeviceProfileSetupForNewAccountLogin;
                settings.DalamudEnabled                       = EnableHooks;
                settings.DalamudInjectionDelayMS              = DalamudInjectionDelayMs ?? 0;
                settings.ManualInjectDelayMs                  = ManualInjectDelayMs     ?? 0;
                settings.DalamudLoadMethod                    = dalamudLoadMethod;
                settings.AdditionalLaunchArgs                 = LaunchArgs;
                settings.DPIAwareness                         = dpiAwareness;
                settings.SpeedLimitBytes                      = speedLimitBytes;
                settings.CredType                             = credTypeApplyResult.AppliedCredType;
            }
        );

        SelectedCredType = credTypeApplyResult.AppliedCredType;

        SettingsSaved?.Invoke(this, EventArgs.Empty);
        return true;
    }
    
    public void AddCompanionApp()
    {
        var result = _dialogService.ShowCompanionAppSetup();
        if (result == null || string.IsNullOrWhiteSpace(result.FilePath))
            return;

        CompanionAppEntries.Add
        (
            new CompanionAppEntry
            {
                IsEnabled = true,
                CompanionApp = result
            }
        );
    }

    public void EditSelectedCompanionApp()
    {
        if (SelectedCompanionAppEntry?.CompanionApp is not CompanionAppConfiguration companionApp)
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

    public void RemoveSelectedCompanionApp()
    {
        if (SelectedCompanionAppEntry == null)
            return;

        CompanionAppEntries.Remove(SelectedCompanionAppEntry);
        SelectedCompanionAppEntry = null;
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

    private void ReplaceCompanionAppEntries(IEnumerable<CompanionAppEntry> entries)
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
