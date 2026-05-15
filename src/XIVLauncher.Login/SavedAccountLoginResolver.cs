using XIVLauncher.Account;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Login;

internal sealed class SavedAccountLoginResolver
(
    AccountManager        accountManager,
    WeGameLoginDataReader weGameLoginDataReader
)
{
    public async Task<ResolvedLoginState?> ResolveAsync(LoginWorkflowRequest request)
    {
        var username              = request.Username;
        var finalLoginType        = request.LoginType;
        var secret                = string.Empty;
        var doingAutoLogin        = request.DoingAutoLogin;
        var selectedArea          = request.CurrentArea;
        var savedAccount          = FindSavedAccount(request.LoginType, username);
        var accountType           = ResolveAccountType(request.LoginType, savedAccount);
        var hasUnavailableSecrets = accountManager.HasUnavailableSecrets(savedAccount);

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

            case LoginType.WeGameAuto:
                doingAutoLogin = true;

                if (request.ReadWeGameInfo)
                {
                    var loginData = await weGameLoginDataReader.ReadAsync(request.LoginCancellationTokenSource, request.Interaction);
                    if (loginData == null)
                        return null;

                    if (string.IsNullOrWhiteSpace(loginData.SndaID) || string.IsNullOrWhiteSpace(loginData.SessionId))
                        throw new Exception("获取 WeGame 登录信息失败");

                    username = loginData.SndaID;
                    secret   = loginData.SessionId;

                    var areaId = loginData.Args
                                          .FirstOrDefault(static argument => argument.Contains("AreaID=", StringComparison.Ordinal))
                                          ?.Split('=')[1];
                    if (!string.IsNullOrWhiteSpace(areaId))
                        selectedArea = request.LoginAreas.FirstOrDefault(area => area.AreaID == areaId) ?? selectedArea;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(username))
                    {
                        request.Interaction.ShowError("错误: 已保存账号的 SndaID 不能为空");
                        return null;
                    }

                    if (savedAccount == null)
                    {
                        request.Interaction.ShowError("未找到该 SndaID 对应的已保存账号, 请勾选 \"重新从 WeGame 读取 SID\" 后再试");
                        return null;
                    }

                    if (hasUnavailableSecrets || string.IsNullOrWhiteSpace(savedAccount.WeGameSIDSecret))
                    {
                        request.Interaction.ShowError("该账号没有可用的已保存 SID, 请勾选 \"重新从 WeGame 读取 SID\" 后再试");
                        return null;
                    }

                    secret = await accountManager.Decrypt(savedAccount.WeGameSIDSecret) ?? string.Empty;
                }

                savedAccount   = FindSavedAccount(LoginType.WeGameAuto, username);
                accountType    = ResolveAccountType(LoginType.WeGameAuto, savedAccount);
                finalLoginType = LoginType.WeGameAuto;
                break;

            case LoginType.WeGameManual:
                if (!hasUnavailableSecrets && string.IsNullOrEmpty(request.Password) && savedAccount?.WeGameTokenSecret is { Length: > 0 } weGameTokenSecret)
                {
                    secret         = await accountManager.CredProvider.Decrypt(weGameTokenSecret) ?? string.Empty;
                    finalLoginType = LoginType.WeGameManual;
                }

                if (string.IsNullOrWhiteSpace(secret))
                {
                    secret         = request.Password;
                    finalLoginType = LoginType.WeGameManual;
                }

                if (string.IsNullOrWhiteSpace(secret))
                {
                    request.Interaction.ShowError("错误: WeGame 自动登录密钥 不能为空");
                    return null;
                }

                break;

            case LoginType.Slide:
                if (!hasUnavailableSecrets && doingAutoLogin && savedAccount?.SdoAutoLoginSessionKey is { Length: > 0 } slideAutoLoginSessionKey)
                {
                    secret         = await accountManager.Decrypt(slideAutoLoginSessionKey) ?? string.Empty;
                    finalLoginType = LoginType.AutoLoginSession;
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
            doingAutoLogin,
            selectedArea,
            savedAccount,
            accountType,
            hasUnavailableSecrets
        );
    }

    private XIVAccount? FindSavedAccount(LoginType loginType, string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        if (loginType == LoginType.AutoLoginSession)
        {
            return accountManager.Accounts.FirstOrDefault(account => string.Equals(account.SdoLoginAccount, username, StringComparison.Ordinal))
                   ?? accountManager.Accounts.FirstOrDefault(account => string.Equals(account.UserName,     username, StringComparison.Ordinal));
        }

        return accountManager.FindAccount(username, loginType.ToAccountType());
    }

    private static XIVAccountType ResolveAccountType(LoginType loginType, XIVAccount? savedAccount) =>
        loginType == LoginType.AutoLoginSession
            ? savedAccount?.AccountType ?? XIVAccountType.Sdo
            : loginType.ToAccountType();
}

internal sealed record ResolvedLoginState
(
    LoginType      RequestedLoginType,
    LoginType      FinalLoginType,
    string         Username,
    string         Secret,
    bool           DoingAutoLogin,
    LoginArea      Area,
    XIVAccount?    SavedAccount,
    XIVAccountType AccountType,
    bool           HasUnavailableSavedSecrets
);
