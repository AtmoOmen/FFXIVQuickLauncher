using System.Diagnostics;
using Serilog;
using XIVLauncher.Account.Cred;
using XIVLauncher.Common.Game;
using XIVLauncher.Login.Models;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel.Main.Models;

namespace XIVLauncher.Windows.ViewModel.Main.Flows;

/// <summary>
///     主界面的启动任务编排与版本设置迁移流
/// </summary>
internal sealed class StartupFlow
{
    private const int CURRENT_VERSION_LEVEL = 2;

    private readonly MainWindowViewModel vm;

    public bool CanRestoreSavedSession =>
        App.Settings.AutoLoginToLastAccount                                                                     &&
        vm.AccountManager.CurrentAccount is { QuickLoginEnabled: true, LoginSessionSecret.Length: > 0 } account &&
        !vm.AccountManager.HasUnavailableSecrets(account)                                                       &&
        vm.AccountManager.CurrentCredType != CredType.WindowsHello;

    public StartupFlow
    (
        MainWindowViewModel vm
    ) =>
        this.vm = vm;

    public async Task RunStartupTasksAsync()
    {
        var lastAccountId = App.AccountManager.CurrentAccountID;

        try
        {
            await SetupServersAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "获取启动大区信息失败，等待手动登录");
            return;
        }

        var loginTask = Task.CompletedTask;

        vm.Window.Dispatcher.Invoke
        (() =>
            {
                vm.LoginPage.IsFastLogin = App.Settings.FastLogin;
                vm.AccountFlow.RequestSwitchToCurrentAccount?.Invoke();

                var account = App.AccountManager.CurrentAccount;

                if (CanRestoreSavedSession                                                           &&
                    !string.IsNullOrWhiteSpace(lastAccountId)                                        &&
                    account is { QuickLoginEnabled: true, LoginSessionSecret.Length: > 0 }           &&
                    string.Equals(account.ID,            lastAccountId,    StringComparison.Ordinal) &&
                    string.Equals(vm.LoginPage.Username, account.UserName, StringComparison.Ordinal) &&
                    vm.LoginPage.Area is { AreaID: not "-1" }                                        &&
                    vm.CurrentGameLaunchContext   == null                                            &&
                    vm.LoginCardTransitionerIndex == (int)LoginCardType.MainPage)
                {
                    loginTask = vm.LoginFlow.StartLoginAsync
                    (
                        account.IsWeGame ?
                            LoginType.WeGame :
                            LoginType.Slide,
                        account.UserName,
                        string.Empty,
                        true,
                        false,
                        LoginAfterAction.Start,
                        true
                    );
                }
            }
        );

        await loginTask.ConfigureAwait(false);
        _ = vm.NewsFlow.RefreshHeadlinesAsync();

        var accountType = App.AccountManager.CurrentAccount?.AccountType ?? App.Settings.SelectedLoginType.ToAccountType(XIVAccountType.Sdo);
        Troubleshooting.LogTroubleshooting(App.Settings.GetGamePath(accountType));
    }

    public static void ApplyStartupDefaults()
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
                             x.ProcessName.Contains
                                 ("rtss", StringComparison.OrdinalIgnoreCase) ||
                             x.ProcessName.Contains("skifsvc64", StringComparison.OrdinalIgnoreCase)
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
