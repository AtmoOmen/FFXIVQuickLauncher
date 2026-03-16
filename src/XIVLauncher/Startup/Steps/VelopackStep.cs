using System.Threading;
using System.Threading.Tasks;
using Velopack;

namespace XIVLauncher.Startup.Steps;

public class VelopackStep : IStartupStep
{
    public string Name => "Velopack 初始化";
    public int Order => 60;

    public Task ExecuteAsync(StartupContext context, CancellationToken cancellationToken = default)
    {
        VelopackApp.Build().Run();
        return Task.CompletedTask;
    }
}
