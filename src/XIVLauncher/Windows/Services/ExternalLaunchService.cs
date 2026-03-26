using System.Diagnostics;

namespace XIVLauncher.Windows.Services;

internal sealed class ExternalLaunchService : IExternalLaunchService
{
    public void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public void OpenPath(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public void OpenExecutable(string path, string? arguments = null)
    {
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = true
        };

        if (!string.IsNullOrWhiteSpace(arguments))
            startInfo.Arguments = arguments;

        Process.Start(startInfo);
    }
}
