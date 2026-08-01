namespace XIVLauncher.Common.Network;

public interface INetworkEnvironmentService
{
    Task<NetworkEnvironmentInfo> GetCurrentAsync(CancellationToken cancellationToken = default);

    Task<NetworkEnvironmentInfo> RefreshAsync(CancellationToken cancellationToken = default);
}
