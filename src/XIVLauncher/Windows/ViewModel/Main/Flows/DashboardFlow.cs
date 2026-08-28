using System.Diagnostics;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Login.Channels;
using XIVLauncher.Login.Client;
using XIVLauncher.Login.Models;
using XIVLauncher.Windows.Services;
using XIVLauncher.Windows.ViewModel.Main.Models;

namespace XIVLauncher.Windows.ViewModel.Main.Flows;

internal sealed class DashboardFlow
(
    MainWindowViewModel vm
)
{
    public void HandleStartGameFromDashboard
    (
        LoginAfterAction action
    )
    {
        if (vm.CurrentGameLaunchContext == null)
            return;

        vm.IsEnabled = false;

        if (action == LoginAfterAction.Start && vm.DashboardPage.IsGameUpdateAvailable)
        {
            _ = Task.Run
            (async () =>
                {
                    try
                    {
                        await vm.GameLaunchFlow.InstallGamePatchAsync(true).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        CustomMessageBox.Builder
                                        .NewFromUnexpectedException(ex, "Dashboard/UpdateGame")
                                        .WithParentWindow(vm.Window)
                                        .Show();
                    }
                    finally
                    {
                        vm.Window.Dispatcher.Invoke
                        (() =>
                            {
                                vm.IsEnabled = true;
                                vm.Activate();
                                vm.SwitchCard(LoginCardType.Dashboard, false);
                            }
                        );
                    }
                }
            );
            return;
        }

        _ = Task.Run
        (async () =>
            {
                try
                {
                    if (await vm.GameLaunchFlow.LaunchGameWithRetryLoop(vm.CurrentGameLaunchContext, action).ConfigureAwait(false) &&
                        App.Settings.ExitLauncherWhenGameExit)
                        Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    vm.Window.Dispatcher.Invoke
                    (() =>
                        {
                            CustomMessageBox.Builder
                                            .NewFromUnexpectedException(ex, "Dashboard/StartGame")
                                            .WithParentWindow(vm.Window)
                                            .Show();
                        }
                    );
                }
                finally
                {
                    vm.Window.Dispatcher.Invoke
                    (() =>
                        {
                            vm.IsEnabled = true;
                            vm.Activate();
                            vm.SwitchCard(LoginCardType.Dashboard, false);
                        }
                    );
                }
            }
        );
    }

    public void HandleSwitchAccount()
    {
        vm.CancelLogin();
        vm.CurrentGameLaunchContext = null;
        vm.SwitchCard(LoginCardType.MainPage);
        vm.AccountSwitcher.RefreshEntries(vm.AccountManager.CurrentAccountID, false);
        vm.AccountFlow.RequestSwitchToCurrentAccount?.Invoke();

        Task.Run(() => { vm.DCTravelRuntimeService.Stop(); });
    }

    public void HandleOpenDeviceProfile()
    {
        var account = vm.AccountManager.CurrentAccount;
        if (account == null)
            return;

        var dialogService = new DialogService(vm.Window);
        dialogService.ShowAccountDeviceProfileSettings(account, vm.AccountManager);
        vm.AccountSwitcher.RefreshEntries(vm.AccountManager.CurrentAccountID, false);
    }

    public async Task HandleOpenAuthenticatedSiteAsync
    (
        string serviceUrl,
        string appId
    )
    {
        try
        {
            var oauth = vm.CurrentGameLaunchContext?.LoginResult.OAuthLogin ?? throw new InvalidOperationException("当前登录上下文不存在");
            if (string.IsNullOrWhiteSpace(oauth.TGT) || string.IsNullOrWhiteSpace(oauth.Guid) || oauth.DeviceProfile == null)
                throw new InvalidOperationException("当前登录会话不支持网页单点登录");

            var loginContext = new LoginChannelContext(oauth.DeviceProfile);
            var loginUri     = await loginContext.GetWebLoginUriAsync(oauth.TGT, oauth.Guid, serviceUrl, appId);
            Process.Start(new ProcessStartInfo(loginUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CustomMessageBox.Builder
                            .NewFrom($"无法打开已登录的官方网站: {ex.Message}")
                            .WithCaption("打开官方网站失败")
                            .WithParentWindow(vm.Window)
                            .Show();
        }
    }

    public void HandleOpenDCTravel()
    {
        if (vm.CurrentGameLaunchContext == null)
            return;

        vm.SwitchCard(LoginCardType.DCTravel);
        _ = vm.DCTravelPage.InitializeAsync(vm.CurrentGameLaunchContext.Area.AreaName);
    }

    public void HandleSetCurrentAreaFromDCTravel
    (
        string areaName
    )
    {
        if (vm.CurrentGameLaunchContext == null)
        {
            Log.Error("[MainWindow] currentGameLaunchContext 为空, 无法切换大区");
            return;
        }

        var matched = vm.CurrentGameLaunchContext.Areas.FirstOrDefault
            (a => string.Equals(a.AreaName, areaName, StringComparison.Ordinal));

        if (matched != null)
        {
            Log.Information
            (
                "[MainWindow] DC Travel 完成，切换大区: {Old} → {New}",
                vm.CurrentGameLaunchContext.Area.AreaName,
                areaName
            );

            vm.CurrentGameLaunchContext.Area = matched;
            vm.DashboardPage.AreaName        = matched.AreaName;
            vm.DashboardPage.AreaStatus      = matched.AreaStatus;
            vm.DashboardPage.SelectedArea    = matched;

            if (App.AccountManager.CurrentAccount != null)
            {
                App.AccountManager.CurrentAccount.AreaName = matched.AreaName;
                App.AccountManager.Save();
            }
        }
    }

    public void HandleSetAreaFromDashboard
    (
        LoginArea area
    )
    {
        if (vm.CurrentGameLaunchContext == null)
            return;

        Log.Information
        (
            "[MainWindow] Dashboard 切换大区: {Old} → {New} (Lobby={Lobby})",
            vm.CurrentGameLaunchContext.Area.AreaName,
            area.AreaName,
            area.AreaLobby
        );

        vm.CurrentGameLaunchContext.Area = area;
        vm.DashboardPage.AreaName        = area.AreaName;
        vm.DashboardPage.AreaStatus      = area.AreaStatus;

        if (App.AccountManager.CurrentAccount != null)
        {
            App.AccountManager.CurrentAccount.AreaName = area.AreaName;
            App.AccountManager.Save();
        }
    }

    public void UpdateDashboardInfo
    (
        LoginResult loginResult
    )
    {
        var oauth = loginResult.OAuthLogin;
        if (oauth == null)
            return;

        vm.DashboardPage.AccountName = oauth.InputUserID;

        RefreshGameVersion();

        if (vm.CurrentGameLaunchContext != null)
        {
            var areas = vm.CurrentGameLaunchContext.Areas;
            vm.DashboardPage.Areas.Clear();
            foreach (var a in areas)
                vm.DashboardPage.Areas.Add(a);

            vm.DashboardPage.SelectedArea = vm.CurrentGameLaunchContext.Area;
            vm.DashboardPage.AreaName     = vm.CurrentGameLaunchContext.Area.AreaName;
            vm.DashboardPage.AreaStatus   = vm.CurrentGameLaunchContext.Area.AreaStatus;
        }
    }

    public void RefreshGameVersion()
    {
        var accountType = vm.CurrentGameLaunchContext?.AccountType ??
                          vm.AccountManager.CurrentAccount?.AccountType ?? vm.LoginPage.LoginTypeOption.LoginType.ToAccountType(XIVAccountType.Sdo);
        var gamePath = App.Settings.GetGamePath(accountType);
        vm.DashboardPage.GameVersion = gamePath != null ?
                                           Repository.Ffxiv.GetVer(gamePath) :
                                           string.Empty;
    }
}
