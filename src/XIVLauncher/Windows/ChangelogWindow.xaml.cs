using System;
using System.Media;
using System.Windows;
using Newtonsoft.Json;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for ErrorWindow.xaml
/// </summary>
public partial class ChangelogWindow : Window
{
    private ChangeLogWindowViewModel Model => (ChangeLogWindowViewModel)DataContext;

    public ChangelogWindow()
    {
        InitializeComponent();

        DiscordButton.Click += SupportLinks.OpenDiscordChannel;
        DataContext         =  new ChangeLogWindowViewModel();

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void UpdateVersion(string version)
    {
        Model.UpdateNotice = string.Format("XIVLauncherCN (Soil) 已更新至 {0}", version);
        Show();
    }

    public new void Show()
    {
        SystemSounds.Asterisk.Play();
        base.Show();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    public class VersionMeta
    {
        [JsonProperty("version")] public string Version { get; set; } = string.Empty;

        [JsonProperty("url")] public string Url { get; set; } = string.Empty;

        [JsonProperty("changelog")] public string Changelog { get; set; } = string.Empty;

        [JsonProperty("when")] public DateTime When { get; set; }
    }

    public class ReleaseMeta
    {
        [JsonProperty("releaseVersion")] public VersionMeta ReleaseVersion { get; set; } = new();

        [JsonProperty("prereleaseVersion")] public VersionMeta PrereleaseVersion { get; set; } = new();
    }
}
