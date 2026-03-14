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
    private ChangeLogWindowViewModel Model => DataContext as ChangeLogWindowViewModel;

    public ChangelogWindow()
    {
        InitializeComponent();

        DiscordButton.Click += SupportLinks.OpenDiscordChannel;

        var vm = new ChangeLogWindowViewModel();
        DataContext = vm;

        ChangeLogText.Text = vm.ChangelogLoadingLoc;

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void UpdateVersion(string version) =>
        UpdateNotice.Text = string.Format(Model.UpdateNoticeLoc, version);

    public new void Show()
    {
        SystemSounds.Asterisk.Play();
        base.Show();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    public class VersionMeta
    {
        [JsonProperty("version")] public string Version { get; set; }

        [JsonProperty("url")] public string Url { get; set; }

        [JsonProperty("changelog")] public string Changelog { get; set; }

        [JsonProperty("when")] public DateTime When { get; set; }
    }

    public class ReleaseMeta
    {
        [JsonProperty("releaseVersion")] public VersionMeta ReleaseVersion { get; set; }

        [JsonProperty("prereleaseVersion")] public VersionMeta PrereleaseVersion { get; set; }
    }
}
