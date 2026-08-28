using XIVLauncher.Account;
using XIVLauncher.Common.Game;
using XIVLauncher.Login.Models;
using XIVLauncher.Windows.ViewModel.Main.Models;
using XIVLauncher.Windows.ViewModel.Main.Services;

namespace XIVLauncher.Windows.ViewModel.Main.Flows;

/// <summary>
///     主界面的账号切换与账号字段操作流
/// </summary>
internal sealed class AccountFlow
{
    public const string PRESUDO_PASSWORD = "********假的密码********";

    private readonly MainWindowViewModel  vm;
    private readonly ClipboardService     clipboardService = new();

    public AccountFlow(MainWindowViewModel vm)
    {
        this.vm = vm;
    }

    /// <summary>
    ///     由 View 赋值, 切换账号时刷新密码框显示内容
    /// </summary>
    public Action<string>? LoginPasswordDisplay { get; set; }

    /// <summary>
    ///     请求切换到当前保存的账号, 由启动任务与仪表盘触发
    /// </summary>
    public Action? RequestSwitchToCurrentAccount { get; set; }

    public void SwitchAccount(XIVAccount account, bool saveAsCurrent)
    {
        if (saveAsCurrent)
            vm.AccountManager.CurrentAccount = account;

        var hasUnavailableSecrets = vm.AccountManager.HasUnavailableSecrets(account);
        var selectedArea          = vm.LoginPage.LoginAreas.FirstOrDefault(x => x.AreaName == account.AreaName);

        vm.LoginPage.IsFastLogin      = account.QuickLoginEnabled;
        vm.LoginPage.Area             = selectedArea ?? vm.LoginPage.Area;
        vm.LoginPage.Password         = string.Empty;
        LoginPasswordDisplay?.Invoke(string.Empty);

        switch (account.AccountType)
        {
            case XIVAccountType.Sdo:
                var nextLoginType = !hasUnavailableSecrets && !string.IsNullOrWhiteSpace(account.SdoPassword)
                                        ? LoginType.Static
                                        : LoginType.Slide;

                vm.LoginPage.SelectLoginType(nextLoginType);
                vm.LoginPage.Username = account.UserName;

                if (nextLoginType == LoginType.Static)
                {
                    // Make users happy by not showing their password
                    LoginPasswordDisplay?.Invoke(PRESUDO_PASSWORD);
                    vm.LoginPage.Password = PRESUDO_PASSWORD;
                }

                break;

            case XIVAccountType.WeGame:
                vm.LoginPage.SelectLoginType(LoginType.WeGame);
                vm.LoginPage.Username = account.WeGameLoginAccount;

                if (!hasUnavailableSecrets && !string.IsNullOrWhiteSpace(account.WeGameQuickLoginSecret))
                {
                    LoginPasswordDisplay?.Invoke(PRESUDO_PASSWORD);
                    vm.LoginPage.Password = PRESUDO_PASSWORD;
                }
                else
                    vm.LoginPage.IsReadWegameInfo = false;

                break;
        }
    }

    public void SwitchAccountFromSwitcher()
    {
        var selectedAccount = vm.AccountSwitcher.SelectCurrentAccount();
        if (selectedAccount == null)
            return;

        SwitchAccount(selectedAccount, true);
        vm.SwitchCard(LoginCardType.MainPage, false);
    }

    public void ClearCurrentAccount()
    {
        vm.AccountManager.ClearCurrentAccount();
        vm.AccountSwitcher.RefreshEntries(null, false);
    }

    public void CopyAccountField(string text)
    {
        var copyThread = new Thread
        (() =>
            {
                var copied = clipboardService.TrySetText(text);
                vm.ShowSnackbar(copied ? $"已复制: {text}" : "复制失败, 剪贴板被占用");
            }
        )
        {
            IsBackground = true,
            Name         = "ClipboardCopy"
        };
        copyThread.Start();
    }
}
