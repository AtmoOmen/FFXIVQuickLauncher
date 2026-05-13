using System.Collections.Frozen;
using Serilog;
using XIVLauncher.Common.Game.DCTravel;
using XIVLauncher.Common.Game.Login.Channels;

namespace XIVLauncher.Common.Game.Login;

public sealed class LoginClient
{
    public async Task<LoginResult> LoginAsync(LoginType loginType, LoginRequest request, CancellationToken cancellationToken = default)
    {
        var channels = DiscoverChannels(new LoginChannelContext(request.DeviceProfile));

        if (!channels.TryGetValue(loginType, out var channel))
            throw new ArgumentOutOfRangeException(nameof(loginType), loginType, $"未知登录渠道: {loginType}");

        cancellationToken.ThrowIfCancellationRequested();
        return await channel.LoginAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LoginResult> LoginBySessionKey
    (
        string                account,
        string                autoLoginSessionKey,
        DCTravelClient?       dcTravelClient,
        DeviceProfileSnapshot deviceProfile
    )
    {
        var request = new LoginRequest
        {
            Account        = account,
            Secret         = autoLoginSessionKey,
            DeviceProfile  = deviceProfile,
            DCTravelClient = dcTravelClient
        };
        return await LoginAsync(LoginType.AutoLoginSession, request).ConfigureAwait(false);
    }

    public async Task<LoginResult> LoginWithPatchCheck
    (
        Func<CancellationToken, Task<LoginResult>> checkGameUpdateAsync,
        LoginType                                  loginType,
        LoginType                                  fallbackLoginType,
        Func<LoginType, LoginRequest>              requestFactory,
        CancellationToken                          cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var checkResult = await checkGameUpdateAsync(cancellationToken).ConfigureAwait(false);
        if (checkResult.State == LoginState.NeedsPatchGame)
            return checkResult;

        if (loginType == LoginType.AutoLoginSession)
        {
            try
            {
                var autoLoginRequest = requestFactory(loginType);
                return await LoginAsync(loginType, autoLoginRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "LoginBySessionKey failed, fallback to {FallbackLoginType}", fallbackLoginType);
                loginType = fallbackLoginType;
            }
        }

        var loginRequest = requestFactory(loginType);
        return await LoginAsync(loginType, loginRequest, cancellationToken).ConfigureAwait(false);
    }

    private static FrozenDictionary<LoginType, ILoginChannel> DiscoverChannels(LoginChannelContext context)
    {
        var loginChannels = typeof(ILoginChannel)
                            .Assembly
                            .GetTypes()
                            .Where(type => type is { IsAbstract: false, IsInterface: false })
                            .Where(type => typeof(ILoginChannel).IsAssignableFrom(type))
                            .Select(type => (ILoginChannel?)Activator.CreateInstance(type, context))
                            .Where(channel => channel != null)
                            .Cast<ILoginChannel>()
                            .ToArray();

        var duplicatedType = loginChannels
                             .GroupBy(channel => channel.Type)
                             .FirstOrDefault(group => group.Count() > 1);

        if (duplicatedType != null)
            throw new InvalidOperationException($"发现重复 LoginType 渠道实现: {duplicatedType.Key}");

        return loginChannels.ToFrozenDictionary(channel => channel.Type);
    }
}
