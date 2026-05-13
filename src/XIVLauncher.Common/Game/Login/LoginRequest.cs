using XIVLauncher.Common.Game.DCTravel;

namespace XIVLauncher.Common.Game.Login;

public sealed class LoginRequest
{
    public string                                 Account                      { get; init; } = string.Empty;
    public string                                 Secret                       { get; init; } = string.Empty;
    public bool                                   AutoLogin                    { get; init; }
    public DeviceProfileSnapshot                  DeviceProfile                { get; init; } = FakeMachineInfo.CreateSnapshot();
    public CancellationTokenSource?               LoginCancellationTokenSource { get; init; }
    public Action<byte[]>?                        ShowQRCode                   { get; init; }
    public Action<string>?                        ShowVerificationCode         { get; init; }
    public Action<string>?                        ShowLoginMessage             { get; init; }
    public Func<string, string, string, string?>? PromptTextInput              { get; init; }
    public Func<LoginCaptchaChallenge, string?>?  PromptCaptchaInput           { get; init; }
    public DCTravelClient?                        DCTravelClient               { get; init; }

    public static LoginRequest Create
    (
        string                                 account,
        string                                 secret,
        bool                                   autoLogin,
        DeviceProfileSnapshot                  deviceProfile,
        DCTravelClient?                        dcTravelClient,
        CancellationTokenSource?               loginCancellationTokenSource,
        Action<byte[]>?                        showQrCode,
        Action<string>?                        showVerificationCode,
        Action<string>?                        showLoginMessage,
        Func<string, string, string, string?>? promptTextInput,
        Func<LoginCaptchaChallenge, string?>?  promptCaptchaInput
    )
    {
        return new LoginRequest
        {
            Account                      = account,
            Secret                       = secret,
            AutoLogin                    = autoLogin,
            DeviceProfile                = deviceProfile,
            DCTravelClient               = dcTravelClient,
            LoginCancellationTokenSource = loginCancellationTokenSource,
            ShowQRCode                   = showQrCode,
            ShowVerificationCode         = showVerificationCode,
            ShowLoginMessage             = showLoginMessage,
            PromptTextInput              = promptTextInput,
            PromptCaptchaInput           = promptCaptchaInput
        };
    }
}
