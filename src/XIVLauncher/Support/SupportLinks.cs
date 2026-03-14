using System.Diagnostics;
using System.Windows;

namespace XIVLauncher.Support;

public static class SupportLinks
{
    public static void OpenDiscord(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://discord.gg/dailyroutines") { UseShellExecute = true });

    public static void OpenFaq(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://ottercorp.github.io/faq") { UseShellExecute = true });

    public static void OpenDiscordChannel(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://discord.gg/dailyroutines") { UseShellExecute = true });
}
