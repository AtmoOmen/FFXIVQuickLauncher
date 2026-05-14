using XIVLauncher.Dalamud;
using XIVLauncher.Support;

namespace XIVLauncher.Startup;

internal sealed class AppDalamudTroubleshootingProvider : IDalamudTroubleshootingProvider
{
    public string GetTroubleshootingJson() =>
        Troubleshooting.GetTroubleshootingJson();
}
