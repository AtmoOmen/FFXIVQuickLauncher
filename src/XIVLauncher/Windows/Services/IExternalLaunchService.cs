namespace XIVLauncher.Windows.Services;

internal interface IExternalLaunchService
{
    void OpenUrl(string url);

    void OpenPath(string path);

    void OpenExecutable(string path, string? arguments = null);
}
