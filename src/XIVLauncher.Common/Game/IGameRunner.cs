using System.Diagnostics;

namespace XIVLauncher.Common.Game;

public interface IGameRunner
{
    Process? Start(string path, string workingDirectory, string arguments, IDictionary<string, string> environment, DPIAwareness dpiAwareness);
}
