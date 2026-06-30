using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Login;
using XIVLauncher.Windows.Services;
using XIVLauncher.Windows.ViewModel.Main.Models;

namespace XIVLauncher.Windows.ViewModel.Main.Handlers;

internal sealed class DashboardFlowHandler
(
    MainWindowViewModel vm
)
{
    public void HandleStartGameFromDashboard(LoginAfterAction action)
    {
        if (vm.CurrentGameLaunchContext == null)
            return;

        vm.IsEnabled = false;

        _ = Task.Run
        (async () =>
            {
                try
                {
                    if (await vm.GameLaunchFlow.LaunchGameWithRetryLoop(vm.CurrentGameLaunchContext, action).ConfigureAwait(false) && App.Settings.ExitLauncherWhenGameExit)
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
        vm.RequestSwitchToCurrentAccount?.Invoke();

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

    public void HandleOpenDCTravel()
    {
        if (vm.CurrentGameLaunchContext == null)
            return;

        vm.SwitchCard(LoginCardType.DCTravel);
        _ = vm.DCTravelPage.InitializeAsync(vm.CurrentGameLaunchContext.Area.AreaName);
    }

    public void HandleSetCurrentAreaFromDCTravel(string areaName)
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

    public void HandleSetAreaFromDashboard(LoginArea area)
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

    public void UpdateDashboardInfo(LoginResult loginResult)
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

    public void RefreshGameVersion() =>
        vm.DashboardPage.GameVersion = App.Settings.GamePath != null
                                        ? Repository.Ffxiv.GetVer(App.Settings.GamePath)
                                        : string.Empty;
}
