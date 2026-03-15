using System;
using System.Threading;
using XIVLauncher.Common.Game.DCTravel;

namespace XIVLauncher.Common.Game.Login;

public sealed class LoginRequest
{
    public string                   Account                      { get; init; } = string.Empty;
    public string                   Secret                       { get; init; } = string.Empty;
    public bool                     AutoLogin                    { get; init; }
    public CancellationTokenSource? LoginCancellationTokenSource { get; init; }
    public Action<byte[]>?          ShowQRCode                   { get; init; }
    public Action<string>?          ShowVerificationCode         { get; init; }
    public DCTravelClient?          DCTravelClient               { get; init; }
}
