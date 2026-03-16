using System.Threading;
using System.Threading.Tasks;

namespace XIVLauncher.Startup;

public interface IStartupStep
{
    string Name { get; }

    int Order { get; }

    Task ExecuteAsync(StartupContext context, CancellationToken cancellationToken = default);
}
