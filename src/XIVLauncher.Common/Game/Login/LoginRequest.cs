using System;
using System.Threading;
using XIVLauncher.Common.Game.DCTravel;

namespace XIVLauncher.Common.Game.Login;

public sealed class LoginRequest
{
    public string                   Account                      { get; init; } = string.Empty;
    public string                   Secret                       { get; init; } = string.Empty;
    public bool                     AutoLogin                    { get; init; }
    public DeviceProfileSnapshot    DeviceProfile                { get; init; } = FakeMachineInfo.CreateSnapshot();
    public CancellationTokenSource? LoginCancellationTokenSource { get; init; }
    public Action<byte[]>?          ShowQRCode                   { get; init; }
    public Action<string>?          ShowVerificationCode         { get; init; }
    public DCTravelClient?          DCTravelClient               { get; init; }

    public static LoginRequest Create
    (
        string                   account,
        string                   secret,
        bool                     autoLogin,
        DeviceProfileSnapshot    deviceProfile,
        DCTravelClient?          dcTravelClient,
        CancellationTokenSource? loginCancellationTokenSource,
        Action<byte[]>?          showQrCode,
        Action<string>?          showVerificationCode
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
            ShowVerificationCode         = showVerificationCode
        };
    }
}
