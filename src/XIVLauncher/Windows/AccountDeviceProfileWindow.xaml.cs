using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using XIVLauncher.Accounts;
using XIVLauncher.Windows.Services;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

public partial class AccountDeviceProfileWindow
{
    private AccountDeviceProfileWindowViewModel ViewModel =>
        (AccountDeviceProfileWindowViewModel)DataContext;

    private const int CURRENT_EXCHANGE_FORMAT_VERSION = 1;

    private static readonly JsonSerializerOptions ExchangeJsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        Encoder                     = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly DialogService dialogService;
    private          Window?       ownerWindow;

    private bool restoreWindowStateAfterOwnerRestore;

    public AccountDeviceProfileWindow(XIVAccount account, AccountManager accountManager, bool isTemporaryAccount = false)
    {
        InitializeComponent();

        dialogService = new DialogService(this);
        DataContext   = new AccountDeviceProfileWindowViewModel(accountManager);
        if (isTemporaryAccount)
            ViewModel.LoadTemporary(account);
        else
            ViewModel.Load(account);
        Title = ViewModel.WindowTitle;

        Loaded += (_, _) => AttachOwnerWindow();
        Closed += (_, _) => DetachOwnerWindow();
    }

    public AccountDeviceProfileWindow(AccountManager accountManager)
    {
        InitializeComponent();

        dialogService = new DialogService(this);
        DataContext   = new AccountDeviceProfileWindowViewModel(accountManager);
        ViewModel.LoadShared();
        Title = ViewModel.WindowTitle;

        Loaded += (_, _) => AttachOwnerWindow();
        Closed += (_, _) => DetachOwnerWindow();
    }

    private void AttachOwnerWindow()
    {
        if (ReferenceEquals(ownerWindow, Owner))
            return;

        DetachOwnerWindow();
        ownerWindow = Owner;

        if (ownerWindow == null)
            return;

        ownerWindow.StateChanged += OwnerWindow_OnStateChanged;
        SyncWindowStateWithOwner();
    }

    private void DetachOwnerWindow()
    {
        if (ownerWindow == null)
            return;

        ownerWindow.StateChanged -= OwnerWindow_OnStateChanged;
        ownerWindow              =  null;
    }

    private void OwnerWindow_OnStateChanged(object? sender, EventArgs e) =>
        SyncWindowStateWithOwner();

    private void SyncWindowStateWithOwner()
    {
        if (ownerWindow == null)
            return;

        if (ownerWindow.WindowState == WindowState.Minimized)
        {
            restoreWindowStateAfterOwnerRestore = WindowState != WindowState.Minimized;
            WindowState                         = WindowState.Minimized;
            return;
        }

        if (!restoreWindowStateAfterOwnerRestore || WindowState != WindowState.Minimized)
            return;

        WindowState                         = WindowState.Normal;
        restoreWindowStateAfterOwnerRestore = false;
    }

    private void RefreshAllButton_OnClick(object sender, RoutedEventArgs e) =>
        ExecuteWithErrorHandling(ViewModel.RefreshAll, "刷新整套设备信息失败。");

    private void UseLocalMachineInfoButton_OnClick(object sender, RoutedEventArgs e) =>
        ExecuteWithErrorHandling(ViewModel.UseLocalMachineInfo, "应用本机设备信息失败。");

    private void RefreshDeviceIdButton_OnClick(object sender, RoutedEventArgs e) =>
        ExecuteWithErrorHandling(ViewModel.RefreshDeviceId, "刷新设备 ID 失败。");

    private void RefreshMacAddressButton_OnClick(object sender, RoutedEventArgs e) =>
        ExecuteWithErrorHandling(ViewModel.RefreshMacAddress, "刷新 MAC 地址失败。");

    private void RefreshHostNameButton_OnClick(object sender, RoutedEventArgs e) =>
        ExecuteWithErrorHandling(ViewModel.RefreshHostName, "刷新主机名失败。");

    private void ImportButton_OnClick(object sender, RoutedEventArgs e) =>
        ExecuteWithErrorHandling(ImportDeviceProfile, "导入设备信息失败。");

    private void ExportButton_OnClick(object sender, RoutedEventArgs e) =>
        ExecuteWithErrorHandling(ExportDeviceProfile, "导出设备信息失败。");

    private void CreatePresetButton_OnClick(object sender, RoutedEventArgs e) =>
        ExecuteWithErrorHandling(ViewModel.CreatePreset, "新增预设失败。");

    private void DeletePresetButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.SelectedPresetId))
        {
            dialogService.ShowMessage
            (
                "请先选择要删除的预设。",
                "设备信息设置",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                false,
                false,
                false,
                false,
                this
            );
            return;
        }

        var result = dialogService.ShowMessage
        (
            $"确认删除当前选中的预设“{ViewModel.PresetRemark}”吗？",
            "设备信息设置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            false,
            false,
            false,
            false,
            this
        );

        if (result != MessageBoxResult.Yes)
            return;

        ExecuteWithErrorHandling(ViewModel.DeletePreset, "删除预设失败。");
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.Save();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            dialogService.ShowMessage
            (
                $"保存设备信息设置失败。\n{ex.Message}",
                "设备信息设置",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                false,
                false,
                false,
                false,
                this
            );
        }
    }

    private void ImportDeviceProfile()
    {
        var dialog = new OpenFileDialog
        {
            Title           = $"导入{ViewModel.ProfileKindText}",
            CheckFileExists = true,
            Multiselect     = false,
            Filter          = "设备信息文件|*.json|所有文件|*.*"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var json = File.ReadAllText(dialog.FileName, Encoding.UTF8);
        var data = JsonSerializer.Deserialize<AccountDeviceProfileExchangeData>(json, ExchangeJsonOptions)
                   ?? throw new InvalidOperationException("导入文件内容为空或格式无效。");

        if (data.Version != CURRENT_EXCHANGE_FORMAT_VERSION)
            throw new InvalidOperationException($"不支持的设备信息文件版本：{data.Version}。");

        ViewModel.Import
        (
            data.DynamicEnabled,
            data.PeriodicRefreshEnabled,
            data.RotationDays,
            data.DeviceId,
            data.MacAddress,
            data.HostName,
            data.GeneratedUtcTicks,
            data.PresetRemark
        );
    }

    private void ExportDeviceProfile()
    {
        var dialog = new SaveFileDialog
        {
            Title           = $"导出{ViewModel.ProfileKindText}",
            AddExtension    = true,
            DefaultExt      = ".json",
            OverwritePrompt = true,
            FileName        = $"{SanitizeFileName(ViewModel.AccountDisplayName)}-{ViewModel.ProfileKindText}.json",
            Filter          = "设备信息文件|*.json|所有文件|*.*"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var json = JsonSerializer.Serialize(CreateExchangeData(), ExchangeJsonOptions);
        File.WriteAllText(dialog.FileName, json, new UTF8Encoding(false));

        dialogService.ShowMessage
        (
            $"设备信息已导出。\n\n保存位置：{dialog.FileName}",
            "设备信息设置",
            showHelpLinks: false,
            showDiscordLink: false,
            showReportLinks: false,
            showOfficialLauncher: false,
            parentWindow: this
        );
    }

    private AccountDeviceProfileExchangeData CreateExchangeData() =>
        new()
        {
            Version                = CURRENT_EXCHANGE_FORMAT_VERSION,
            AccountDisplayName     = ViewModel.AccountDisplayName,
            ExportedAtUtc          = DateTimeOffset.UtcNow,
            DynamicEnabled         = ViewModel.DynamicEnabled,
            PeriodicRefreshEnabled = ViewModel.PeriodicRefreshEnabled,
            RotationDays           = ViewModel.RotationDays,
            GeneratedUtcTicks      = ViewModel.GeneratedUtcTicks,
            PresetRemark           = ViewModel.PresetRemark,
            DeviceId               = ViewModel.DeviceId,
            MacAddress             = ViewModel.MacAddress,
            HostName               = ViewModel.HostName
        };

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized    = new string(fileName.Trim().Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "账号" : sanitized;
    }

    private void ExecuteWithErrorHandling(Action action, string title)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            dialogService.ShowMessage
            (
                $"{title}\n{ex.Message}",
                "设备信息设置",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                false,
                false,
                false,
                false,
                this
            );
        }
    }

    private sealed class AccountDeviceProfileExchangeData
    {
        public int            Version                { get; init; }
        public string         AccountDisplayName     { get; init; } = string.Empty;
        public DateTimeOffset ExportedAtUtc          { get; init; }
        public bool           DynamicEnabled         { get; init; }
        public bool           PeriodicRefreshEnabled { get; init; }
        public int            RotationDays           { get; init; }
        public long           GeneratedUtcTicks      { get; init; }
        public string         PresetRemark           { get; init; } = string.Empty;
        public string         DeviceId               { get; init; } = string.Empty;
        public string         MacAddress             { get; init; } = string.Empty;
        public string         HostName               { get; init; } = string.Empty;
    }
}
