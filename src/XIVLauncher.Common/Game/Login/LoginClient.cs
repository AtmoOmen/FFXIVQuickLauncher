using System;
using System.Collections.Frozen;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Game.DCTravel;
using XIVLauncher.Common.Game.Login.Channels;

namespace XIVLauncher.Common.Game.Login;

public sealed class LoginClient
{
    private FrozenDictionary<LoginType, ILoginChannel> Channels       { get; set; }
    private LoginChannelContext                        ChannelContext { get; set; }

    public LoginClient()
    {
        ChannelContext = new();
        Channels       = DiscoverChannels(ChannelContext);
    }

    public async Task<LoginResult> LoginAsync(LoginType loginType, LoginRequest request, CancellationToken cancellationToken = default)
    {
        if (!Channels.TryGetValue(loginType, out var channel))
            throw new ArgumentOutOfRangeException(nameof(loginType), loginType, $"未知登录渠道: {loginType}");

        cancellationToken.ThrowIfCancellationRequested();
        return await channel.LoginAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LoginResult> LoginBySessionKey(string account, string autoLoginSessionKey, DCTravelClient? dcTravelClient)
    {
        var request = new LoginRequest
        {
            Account        = account,
            Secret         = autoLoginSessionKey,
            DcTravelClient = dcTravelClient
        };
        return await LoginAsync(LoginType.AutoLoginSession, request).ConfigureAwait(false);
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
