using System.IO;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Windows;
using XIVLauncher.Support;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Factories;

public sealed class DalamudLauncherFactory
{
    public static DalamudLauncher Create(DirectoryInfo gamePath, DalamudLoadMethod loadMethod, bool noPlugins, bool noThird) =>
        new
        (
            new WindowsDalamudRunner(),
            App.DalamudUpdater,
            loadMethod,
            gamePath,
            new DirectoryInfo(Paths.RoamingPath),
            new DirectoryInfo(Paths.RoamingPath),
            ClientLanguage.ChineseSimplified,
            (int)App.Settings.DalamudInjectionDelayMs,
            false,
            noPlugins,
            noThird,
            Troubleshooting.GetTroubleshootingJson()
        );
}
