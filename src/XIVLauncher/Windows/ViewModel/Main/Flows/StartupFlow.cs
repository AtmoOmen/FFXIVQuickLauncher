using System.Diagnostics;
using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Common.Game;
using XIVLauncher.Login.Models;
using XIVLauncher.Support;

namespace XIVLauncher.Windows.ViewModel.Main.Flows;

/// <summary>
///     主界面的启动任务编排与版本设置迁移流
/// </summary>
internal sealed class StartupFlow
{
    private const int CURRENT_VERSION_LEVEL = 2;

    private readonly MainWindowViewModel vm;

    public StartupFlow(MainWindowViewModel vm)
    {
        this.vm = vm;
    }

    public async Task RunStartupTasksAsync()
    {
        await SetupServersAsync().ConfigureAwait(false);

        vm.Window.Dispatcher.Invoke
        (() =>
            {
                vm.LoginPage.IsFastLogin = App.Settings.FastLogin;
                vm.AccountFlow.RequestSwitchToCurrentAccount?.Invoke();
            }
        );

        await vm.NewsFlow.RefreshHeadlinesAsync().ConfigureAwait(false);

        var accountType = App.AccountManager.CurrentAccount?.AccountType
                          ?? App.Settings.SelectedLoginType.ToAccountType(XIVAccountType.Sdo);
        Troubleshooting.LogTroubleshooting(App.Settings.GetGamePath(accountType));
    }

    public void ApplyStartupDefaults()
    {
        var versionLevel = App.Settings.VersionUpgradeLevel;

        while (versionLevel < CURRENT_VERSION_LEVEL)
        {
            switch (versionLevel)
            {
                case 0:
                    // Check for RTSS & Special K injectors
                    try
                    {
                        var hasRtss = Process.GetProcesses().Any
                        (x =>
                             x.ProcessName.ToLowerInvariant().Contains("rtss") || x.ProcessName.ToLowerInvariant().Contains("skifsvc64")
                        );

                        if (hasRtss)
                        {
                            App.Settings.DalamudInjectionDelayMS = 4000;
                            Log.Information("RTSS/SpecialK detected, setting delay");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Could not check for RTSS/SpecialK");
                    }

                    break;

                // 5.12.2022: Bad main window placement when using auto-launch
                case 1:
                    App.Settings.MainWindowPlacement = null;
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            versionLevel++;
        }

        App.Settings.VersionUpgradeLevel = versionLevel;
    }

    public void ShowCredTypeRecoveryMessage()
    {
        var result = App.StartupContext.CredTypeApplyResult;
        if (result is not { WasFallbackApplied: true } || string.IsNullOrWhiteSpace(result.UserMessage))
            return;

        CustomMessageBox.Builder
                        .NewFrom(result.UserMessage)
                        .WithCaption("自动登录加密方式已恢复")
                        .WithParentWindow(vm.Window)
                        .Show();
    }

    private async Task SetupServersAsync()
    {
        var areas = new LoginArea[] { new() { AreaName = "获取大区失败", AreaID = "-1" } };
        areas = await LoginArea.Get();

        vm.Window.Dispatcher.Invoke
        (() =>
            {
                if (areas.Length == 0)
                    areas = [new LoginArea { AreaName = "获取大区失败", AreaID = "-1" }];

                vm.LoginPage.LoginAreas = [.. areas];
                vm.LoginPage.Area       = vm.LoginPage.LoginAreas[0];
            }
        );
    }
}
