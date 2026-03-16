using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using XIVLauncher.Common;

namespace XIVLauncher.Startup.Steps;

public class RenderModeStep : IStartupStep
{
    public string Name  => "渲染模式初始化";
    public int    Order => 10;

    public Task ExecuteAsync(StartupContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!EnvironmentSettings.IsHardwareRendered)
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
        catch
        {
            // ignored
        }

        return Task.CompletedTask;
    }
}
