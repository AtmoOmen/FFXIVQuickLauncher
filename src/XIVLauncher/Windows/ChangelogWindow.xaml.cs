using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Documents;
using MdXaml;
using Newtonsoft.Json;
using XIVLauncher.Common.Constant;
using XIVLauncher.Windows.ViewModel;
using XIVLauncher.Xaml.Components;

namespace XIVLauncher.Windows;

/// <summary>
///     更新日志窗口。
/// </summary>
public partial class ChangelogWindow : ChromeWindow
{
    private ChangeLogWindowViewModel Model => (ChangeLogWindowViewModel)DataContext;

    public ChangelogWindow()
    {
        InitializeComponent();

        DiscordButton.Click += (_, _) => Process.Start(new ProcessStartInfo(Links.DISCORD_URL) { UseShellExecute = true });
        DataContext         =  new ChangeLogWindowViewModel();
        Model.ChangeLogText =  File.ReadAllText(Path.Combine(Paths.ResourcesPath, "CHANGELOG.txt"));
        DependencyPropertyDescriptor.FromProperty(MarkdownScrollViewer.MarkdownProperty, typeof(MarkdownScrollViewer))
                                    ?.AddValueChanged(ChangeLogText, (_, _) => RestyleListMarkers());

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void RestyleListMarkers()
    {
        Dispatcher.BeginInvoke
        (
            () =>
            {
                var lists      = new List<List>();
                var paragraphs = new List<Paragraph>();
                CollectListsAndParagraphs(ChangeLogText.Document.Blocks, lists, paragraphs);

                foreach (var list in lists)
                    list.MarkerStyle = TextMarkerStyle.None;

                foreach (var paragraph in paragraphs)
                {
                    if (paragraph.Inlines.FirstInline is Run { Text: { } text } run && !text.StartsWith("•", StringComparison.Ordinal))
                    {
                        paragraph.Inlines.InsertBefore(run, new Run("•\u2002"));
                        paragraph.Padding    = new Thickness(21, 0, 0, 0);
                        paragraph.TextIndent = -21;
                    }
                }
            }
        );
    }

    private static void CollectListsAndParagraphs(BlockCollection blocks, List<List> lists, List<Paragraph> paragraphs)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case List list:
                    lists.Add(list);
                    foreach (var item in list.ListItems)
                        CollectListsAndParagraphs(item.Blocks, lists, paragraphs);
                    break;
                case Paragraph paragraph:
                    paragraphs.Add(paragraph);
                    break;
            }
        }
    }

    public void UpdateVersion(string version)
    {
        Model.UpdateNotice = "XIVLauncherCN (Soil) 已更新至新版本";
        Model.VersionText  = $"v{version}";
    }

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
