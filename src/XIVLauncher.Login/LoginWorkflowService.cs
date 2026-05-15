using XIVLauncher.Account;
using XIVLauncher.Account.DeviceProfiles;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Login;

public sealed class LoginWorkflowService
{
    private readonly AccountManager                     accountManager;
    private readonly LoginClient                        loginClient = new();
    private readonly SavedAccountLoginResolver          savedAccountLoginResolver;
    private readonly NewAccountDeviceProfileCoordinator newAccountDeviceProfileCoordinator;

    public LoginWorkflowService(AccountManager accountManager, IWeGameTokenCaptureCoordinator weGameTokenCaptureCoordinator)
    {
        this.accountManager                = accountManager;
        savedAccountLoginResolver          = new(accountManager, weGameTokenCaptureCoordinator);
        newAccountDeviceProfileCoordinator = new(accountManager);
    }

    public async Task<LoginWorkflowResult?> ExecuteAsync(LoginWorkflowRequest request)
    {
        var resolvedLoginState = await savedAccountLoginResolver.ResolveAsync(request);
        if (resolvedLoginState == null)
            return null;

        var deviceProfilePreparation = newAccountDeviceProfileCoordinator.Prepare(request, resolvedLoginState);
        if (deviceProfilePreparation == null)
            return null;

        var deviceProfileSnapshot = deviceProfilePreparation.ResolvedDeviceProfile.Snapshot;
        var loginResult = await LoginAsync
                          (
                              request,
                              resolvedLoginState.FinalLoginType,
                              resolvedLoginState.RequestedLoginType,
                              resolvedLoginState.Username,
                              resolvedLoginState.Secret,
                              deviceProfilePreparation.LoginQuickLoginEnabled,
                              deviceProfileSnapshot
                          ).ConfigureAwait(false);

        var postQrResult = newAccountDeviceProfileCoordinator.ApplyAfterQrLogin(request, deviceProfilePreparation, resolvedLoginState, loginResult);
        if (postQrResult == null)
            return null;

        loginResult = postQrResult.LoginResult;
        var resolvedDeviceProfile = postQrResult.ResolvedDeviceProfile;
        var pendingNewAccount     = postQrResult.PendingNewAccount;
        deviceProfileSnapshot = resolvedDeviceProfile.Snapshot;

        if (deviceProfilePreparation.RequiresNewAccountDeviceProfileSetup
            && resolvedLoginState.RequestedLoginType          == LoginType.QRCode
            && loginResult.State                              == LoginState.Ok
            && loginResult.OAuthLogin                         != null
            && pendingNewAccount?.DeviceProfileDynamicEnabled == true)
        {
            var oAuthLogin = loginResult.OAuthLogin;
            loginResult = await LoginAsync
                          (
                              request,
                              LoginType.QuickLogin,
                              LoginType.QuickLogin,
                              oAuthLogin.InputUserID,
                              oAuthLogin.QuickLoginSecret!,
                              true,
                              deviceProfileSnapshot
                          ).ConfigureAwait(false);
        }

        Func<Task<string>>? refreshGameSessionIdByQuickLoginFunc = null;
        var                 shouldShowQuickLoginDisclaimer       = false;
        var                 isAccountPersisted                  = false;

        if (loginResult.State == LoginState.Ok)
        {
            var accountToSave = await SaveAccountAsync(request, resolvedLoginState, resolvedDeviceProfile, pendingNewAccount, loginResult, deviceProfileSnapshot);

            if (accountToSave != null)
            {
                isAccountPersisted            = true;
                shouldShowQuickLoginDisclaimer = accountToSave.QuickLoginEnabled && resolvedLoginState.SavedAccount?.QuickLoginEnabled != true;

                if (accountToSave.AccountType == XIVAccountType.Sdo
                    && accountToSave.QuickLoginEnabled
                    && loginResult.OAuthLogin?.QuickLoginSecret is { Length: > 0 } autoLoginSessionKey)
                {
                    var refreshUsername = resolvedLoginState.Username;
                    refreshGameSessionIdByQuickLoginFunc = async () =>
                    {
                        var newLoginResult = await loginClient.LoginBySessionKey
                                             (
                                                 refreshUsername,
                                                 autoLoginSessionKey,
                                                 request.LoginSessionRefreshSink,
                                                 deviceProfileSnapshot
                                             ).ConfigureAwait(false);
                        return newLoginResult.OAuthLogin?.SessionID ?? string.Empty;
                    };
                }
                else if (accountToSave.AccountType == XIVAccountType.WeGame
                         && accountToSave.QuickLoginEnabled
                         && resolvedLoginState.FinalLoginType == LoginType.WeGame
                         && loginResult.OAuthLogin?.InputUserID is { Length: > 0 } inputUserId
                         && resolvedLoginState.Secret is { Length: > 0 } weGameToken)
                {
                    refreshGameSessionIdByQuickLoginFunc = async () =>
                    {
                        var newLoginResult = await loginClient.LoginAsync
                                             (
                                                 LoginType.WeGame,
                                                 new LoginRequest
                                                 {
                                                     Account                 = inputUserId,
                                                     Secret                  = weGameToken,
                                                     QuickLoginEnabled       = false,
                                                     DeviceProfile           = deviceProfileSnapshot,
                                                     LoginSessionRefreshSink = request.LoginSessionRefreshSink
                                                 }
                                             ).ConfigureAwait(false);
                        return newLoginResult.OAuthLogin?.SessionID ?? string.Empty;
                    };
                }

            }
        }

        return new LoginWorkflowResult
        {
            GameLaunchContext                   = new GameLaunchContext(loginResult, resolvedLoginState.Area, request.LoginAreas),
            IsAccountPersisted                  = isAccountPersisted,
            ShouldShowQuickLoginDisclaimer      = shouldShowQuickLoginDisclaimer,
            UsedSavedWeGameToken                = resolvedLoginState.RequestedLoginType == LoginType.WeGame && resolvedLoginState.UsedSavedCredential,
            RefreshGameSessionIdByQuickLoginFunc = refreshGameSessionIdByQuickLoginFunc
        };
    }

    private Task<LoginResult> LoginAsync
    (
        LoginWorkflowRequest  request,
        LoginType             type,
        LoginType             fallbackLoginType,
        string                username,
        string                secret,
        bool                  quickLoginEnabled,
        DeviceProfileSnapshot deviceProfile
    ) =>
        loginClient.LoginWithPatchCheck
        (
            request.CheckGameUpdateAsync,
            type,
            fallbackLoginType,
            requestLoginType => LoginRequest.Create
            (
                username,
                secret,
                quickLoginEnabled,
                deviceProfile,
                request.LoginSessionRefreshSink,
                request.LoginCancellationTokenSource,
                qrBytes =>
                {
                    if (requestLoginType == LoginType.QRCode)
                        request.Interaction.ShowQrCode(qrBytes);
                },
                code =>
                {
                    if (requestLoginType == LoginType.Slide)
                        request.Interaction.ShowVerificationCode(code);
                },
                request.Interaction.ShowLoginMessage,
                request.Interaction.PromptTextInput,
                request.Interaction.PromptCaptchaInput
            ),
            request.LoginCancellationTokenSource.Token
        );

    private async Task<XIVAccount?> SaveAccountAsync
    (
        LoginWorkflowRequest  request,
        ResolvedLoginState    resolvedLoginState,
        ResolvedDeviceProfile resolvedDeviceProfile,
        XIVAccount?           pendingNewAccount,
        LoginResult           loginResult,
        DeviceProfileSnapshot deviceProfileSnapshot
    )
    {
        var oAuthLogin = loginResult.OAuthLogin;
        if (oAuthLogin == null)
            return null;

        var deviceProfileAccount = pendingNewAccount ?? resolvedLoginState.SavedAccount;
        var accountToSave = new XIVAccount
        {
            QuickLoginEnabled                  = resolvedLoginState.RequestedLoginType == LoginType.WeGame || resolvedLoginState.QuickLoginEnabled,
            SdoLoginAccount                    = oAuthLogin.InputUserID,
            WeGameLoginAccount                 = oAuthLogin.InputUserID,
            AccountType                        = resolvedLoginState.AccountType,
            AreaName                           = resolvedLoginState.Area.AreaName,
            UserDefinedName                    = deviceProfileAccount?.UserDefinedName                    ?? null!,
            DeviceProfilePresetId              = deviceProfileAccount?.DeviceProfilePresetId              ?? string.Empty,
            DeviceProfileDynamicEnabled        = deviceProfileAccount?.DeviceProfileDynamicEnabled        ?? false,
            IsDeviceProfileRotation            = deviceProfileAccount?.IsDeviceProfileRotation            ?? true,
            DeviceProfileRotationDays          = deviceProfileAccount?.DeviceProfileRotationDays          ?? AccountManager.DEFAULT_DEVICE_PROFILE_ROTATION_DAYS,
            DeviceProfileLastGeneratedUtcTicks = deviceProfileAccount?.DeviceProfileLastGeneratedUtcTicks ?? 0
        };

        AccountManager.ApplyResolvedDeviceProfile(accountToSave, resolvedDeviceProfile);

        if (resolvedLoginState.QuickLoginEnabled && accountToSave.AccountType == XIVAccountType.Sdo)
        {
            if (!string.IsNullOrEmpty(oAuthLogin.QuickLoginSecret))
                accountToSave.SdoQuickLoginSecret = await accountManager.Encrypt(oAuthLogin.QuickLoginSecret) ?? string.Empty;

            if (resolvedLoginState.FinalLoginType == LoginType.Static)
                accountToSave.SdoPassword = await accountManager.Encrypt(resolvedLoginState.Secret) ?? string.Empty;
        }

        if (resolvedLoginState.FinalLoginType == LoginType.WeGame && resolvedLoginState.QuickLoginEnabled)
            accountToSave.WeGameQuickLoginSecret = await accountManager.Encrypt(resolvedLoginState.Secret);

        accountToSave.GenerateID();
        accountManager.AddAccount(accountToSave);
        accountManager.CurrentAccount = accountToSave;
        accountManager.Save();
        await accountManager.CredProvider.ClearCache();
        return accountToSave;
    }

}
