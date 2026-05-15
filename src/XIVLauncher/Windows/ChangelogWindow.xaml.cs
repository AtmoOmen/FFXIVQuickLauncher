using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using Newtonsoft.Json;
using XIVLauncher.Common.Constant;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

/// <summary>
///     更新日志窗口。
/// </summary>
public partial class ChangelogWindow : Window
{
    private ChangeLogWindowViewModel Model => (ChangeLogWindowViewModel)DataContext;

    public ChangelogWindow()
    {
        InitializeComponent();

        DiscordButton.Click += (_, _) => Process.Start(new ProcessStartInfo(Links.DISCORD_URL) { UseShellExecute = true });
        DataContext         =  new ChangeLogWindowViewModel();
        Model.ChangeLogText =  File.ReadAllText(Path.Combine(Paths.ResourcesPath, "CHANGELOG.txt"));

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void UpdateVersion(string version) =>
        Model.UpdateNotice = $"XIVLauncherCN (Soil) 已更新至 {version}";

    public new void Show()
    {
        PlayOpenSound();
        base.Show();
    }

    public new bool? ShowDialog()
    {
        PlayOpenSound();
        return base.ShowDialog();
    }

    private static void PlayOpenSound() =>
        SystemSounds.Asterisk.Play();

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    public class VersionMeta
    {
        [JsonProperty("version")]
        public string Version { get; set; } = string.Empty;

        [JsonProperty("url")]
        public string Url { get; set; } = string.Empty;

        [JsonProperty("changelog")]
        public string Changelog { get; set; } = string.Empty;

        [JsonProperty("when")]
        public DateTime When { get; set; }
    }

    public class ReleaseMeta
    {
        [JsonProperty("releaseVersion")]
        public VersionMeta ReleaseVersion { get; set; } = new();

        [JsonProperty("prereleaseVersion")]
        public VersionMeta PrereleaseVersion { get; set; } = new();
    }
}
