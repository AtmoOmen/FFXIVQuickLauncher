using System.IO;
using XIVLauncher.Dalamud;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Factories;

public sealed class DalamudLauncherFactory
{
    public static DalamudSession Create(DirectoryInfo gamePath, DalamudLoadMethod loadMethod, bool noPlugins, bool noThird) =>
        App.Dalamud.CreateLauncher
        (
            gamePath,
            new DalamudLaunchOptions
            (
                loadMethod,
                (int)App.Settings.DalamudInjectionDelayMS,
                false,
                noPlugins,
                noThird
            )
        );
}
