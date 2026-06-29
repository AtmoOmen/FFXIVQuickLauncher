using Serilog;
using XIVLauncher.Account;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Login;

internal sealed class SavedAccountLoginResolver
(
    AccountManager                 accountManager,
    IWeGameTokenCaptureCoordinator weGameTokenCaptureCoordinator
)
{
    public async Task<ResolvedLoginState?> ResolveAsync(LoginWorkflowRequest request)
    {
        var username              = request.Username;
        var finalLoginType        = request.LoginType;
        var secret                = string.Empty;
        var quickLoginEnabled     = request.QuickLoginEnabled;
        var selectedArea          = request.CurrentArea;
        var savedAccount          = FindSavedAccount(request.LoginType, username);
        var accountType           = ResolveAccountType(request.LoginType, savedAccount);
        var hasUnavailableSecrets = accountManager.HasUnavailableSecrets(savedAccount);
        var usedSavedCredential   = false;

        switch (request.LoginType)
        {
            case LoginType.Static:
                if (!string.IsNullOrEmpty(request.Password))
                    secret = request.Password;
                else if (!hasUnavailableSecrets && savedAccount?.SdoPassword is { Length: > 0 } savedPassword)
                    secret = await accountManager.Decrypt(savedPassword) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(username))
                {
                    request.Interaction.ShowError("错误: 静态登录用户名 不能为空");
                    return null;
                }

                if (string.IsNullOrWhiteSpace(secret))
                {
                    request.Interaction.ShowError("错误: 静态登录密码 不能为空");
                    return null;
                }

                break;

            case LoginType.WeGame:
                if (!request.ForceWeGameTokenRecapture
                    && !hasUnavailableSecrets
                    && string.IsNullOrEmpty(request.Password)
                    && savedAccount?.WeGameQuickLoginSecret is { Length: > 0 } weGameQuickLoginSecret)
                {
                    secret         = await accountManager.Decrypt(weGameQuickLoginSecret) ?? string.Empty;
                    username       = savedAccount?.WeGameLoginAccount ?? username;
                    finalLoginType = LoginType.WeGame;
                    usedSavedCredential = !string.IsNullOrWhiteSpace(secret);
                    Log.Information
                    (
                        "[LoginResolver] WeGame 令牌 {Status}, 账号={Account}",
                        usedSavedCredential ? "解密成功" : "解密失败返回空",
                        username
                    );
                }

                if (string.IsNullOrWhiteSpace(secret))
                {
                    if (!string.IsNullOrWhiteSpace(request.Password))
                    {
                        secret         = request.Password;
                        finalLoginType = LoginType.WeGame;
                        Log.Information("[LoginResolver] 使用用户手动输入的 WeGame 令牌");
                    }
                    else
                    {
                        Log.Information("[LoginResolver] 未找到可用令牌, 启动 WeGame 抓取流程");
                        var captureResult = await weGameTokenCaptureCoordinator.CaptureAsync(request.Interaction, request.LoginCancellationTokenSource);
                        if (captureResult == null)
                            return null;

                        username       = captureResult.UserId;
                        secret         = captureResult.Token;
                        finalLoginType = LoginType.WeGame;
                        Log.Information("[LoginResolver] WeGame 抓取成功, 账号={UserId}", username);
                    }
                }

                if (string.IsNullOrWhiteSpace(secret))
                {
                    request.Interaction.ShowError("错误: WeGame 自动登录密钥 不能为空");
                    return null;
                }

                savedAccount = accountManager.Accounts.FirstOrDefault
                (
                    account => account.AccountType == XIVAccountType.WeGame
                               && string.Equals(account.WeGameLoginAccount, username, StringComparison.Ordinal)
                );

                accountType = XIVAccountType.WeGame;

                break;

            case LoginType.Slide:
                if (!hasUnavailableSecrets && quickLoginEnabled && savedAccount?.SdoQuickLoginSecret is { Length: > 0 } slideQuickLoginSecret)
                {
                    secret         = await accountManager.Decrypt(slideQuickLoginSecret) ?? string.Empty;
                    finalLoginType = LoginType.QuickLogin;
                }

                if (string.IsNullOrWhiteSpace(secret))
                    finalLoginType = LoginType.Slide;

                if (string.IsNullOrWhiteSpace(username))
                {
                    request.Interaction.ShowError("错误: 一键登录用户名 不能为空");
                    return null;
                }

                break;

            case LoginType.QRCode:
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }

        return new ResolvedLoginState
        (
            request.LoginType,
            finalLoginType,
            username,
            secret,
            quickLoginEnabled,
            selectedArea,
            savedAccount,
            accountType,
            hasUnavailableSecrets,
            usedSavedCredential
        );
    }

    private XIVAccount? FindSavedAccount(LoginType loginType, string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        if (loginType == LoginType.QuickLogin)
        {
            return accountManager.Accounts.FirstOrDefault(account => string.Equals(account.SdoLoginAccount, username, StringComparison.Ordinal))
                   ?? accountManager.Accounts.FirstOrDefault(account => string.Equals(account.UserName,     username, StringComparison.Ordinal));
        }

        if (loginType == LoginType.WeGame)
        {
            return accountManager.Accounts.FirstOrDefault
            (
                account => account.AccountType == XIVAccountType.WeGame
                           && string.Equals(account.WeGameLoginAccount, username, StringComparison.Ordinal)
            );
        }

        return accountManager.FindAccount(username, loginType.ToAccountType());
    }

    private static XIVAccountType ResolveAccountType(LoginType loginType, XIVAccount? savedAccount) =>
        loginType == LoginType.QuickLogin
            ? savedAccount?.AccountType ?? XIVAccountType.Sdo
            : loginType.ToAccountType();
}

internal sealed record ResolvedLoginState
(
    LoginType      RequestedLoginType,
    LoginType      FinalLoginType,
    string         Username,
    string         Secret,
    bool           QuickLoginEnabled,
    LoginArea      Area,
    XIVAccount?    SavedAccount,
    XIVAccountType AccountType,
    bool           HasUnavailableSavedSecrets,
    bool           UsedSavedCredential = false
);
