using System.Diagnostics;
using System.Windows;

namespace XIVLauncher.Support;

public static class SupportLinks
{
    public static void OpenDiscordChannel(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://discord.gg/dailyroutines") { UseShellExecute = true });
}
